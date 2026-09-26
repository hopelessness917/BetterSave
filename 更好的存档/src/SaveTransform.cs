using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SaveOpt
{
    internal static class SaveTransform
    {
        private const int RequiredVerifiedSaves = 2;
        private const bool AbMode = true;

        private const bool RunOriginal = true;
        private const bool SkipOriginal = false;

        private sealed class TypeInfo
        {
            internal byte[] Name;
            internal bool Skipped;
        }

        private static Type skipAttr;
        private static FieldInfo managersField;
        private static bool registryEmpty;
        private static bool enabled;
        private static bool disabled;
        private static bool warnedRegistry;
        private static bool useRewrite;
        private static int armIndex;
        private static long takenOver;
        private static long verifyObjects;
        private static long mismatchObjects;
        private static long fallbacks;
        private static int verifiedSaves;
        private static int depth;
        private static int maxDepth;

        private static bool pending;
        private static long pendingPos;
        private static int pendingLen;

        private static readonly byte[] ScratchBuffer = new byte[1048576];
        private static PooledStream scratch;
        private static BinaryWriter scratchWriter;

        private static bool[] writeBuf = new bool[64];
        private static ISaveLoadableDetails[] detailBuf = new ISaveLoadableDetails[64];
        private static TypeInfo[] infoBuf = new TypeInfo[64];

        private static readonly Dictionary<Type, TypeInfo> Types = new Dictionary<Type, TypeInfo>();
        private static Type lastType;
        private static TypeInfo lastInfo;

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(SaveLoadRoot), "SaveWithoutTransform",
                new[] { typeof(BinaryWriter) });
            if (target == null)
            {
                Debug.LogError("[更好的存档] 找不到 SaveLoadRoot.SaveWithoutTransform，序列化替换未挂载");
                return false;
            }

            skipAttr = AccessTools.TypeByName("SkipSaveFileSerialization");
            managersField = AccessTools.Field(typeof(SaveLoadRoot), "serializableComponentManagers");
            if (skipAttr == null || managersField == null)
            {
                Debug.LogError("[更好的存档] SkipSaveFileSerialization/serializableComponentManagers 解析失败"
                    + "（属性=" + (skipAttr == null ? "缺失" : "OK")
                    + " 字段=" + (managersField == null ? "缺失" : "OK") + "），序列化替换未挂载");
                return false;
            }

            scratch = new PooledStream(ScratchBuffer);
            scratchWriter = new BinaryWriter(scratch);

            harmony.Patch(target,
                prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SaveTransform), "Prefix")),
                postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SaveTransform), "After")));
            Debug.Log("[更好的存档] 序列化替换已挂载（校验通过后接管；A/B 交替=" + (AbMode ? "开" : "关") + "）");
            return true;
        }

        internal static void BeginSave()
        {
            mismatchObjects = 0;
            verifyObjects = 0;
            pending = false;
            depth = 0;
            useRewrite = false;

            if (enabled)
            {
                armIndex++;
                useRewrite = !AbMode || (armIndex % 2) == 1;
            }

            registryEmpty = false;
            try
            {
                System.Collections.ICollection c = managersField.GetValue(null) as System.Collections.ICollection;
                registryEmpty = c != null && c.Count == 0;
            }
            catch (Exception e)
            {
                Diag.Trace("[更好的存档] 注册表读取失败: " + e.GetType().Name);
            }

            if (!registryEmpty && !warnedRegistry)
            {
                warnedRegistry = true;
                Debug.LogWarning("[更好的存档] serializableComponentManagers 非空，序列化替换永久停用（保持原版行为）");
                disabled = true;
            }
        }

        internal static void EndSave()
        {
            depth = 0;
            pending = false;
            if (disabled || enabled) return;

            if (mismatchObjects == 0 && verifyObjects > 0)
            {
                verifiedSaves++;
                if (verifiedSaves >= RequiredVerifiedSaves)
                {
                    enabled = true;
                    Debug.Log("[更好的存档] 序列化替换版已通过 " + verifiedSaves + " 次存档逐字节校验（本次 "
                        + verifyObjects + " 个对象，最大重入深度 " + maxDepth + "，类型表 "
                        + Types.Count + " 项），下次存档起接管主线程序列化");
                }
            }
        }

        internal static string Status()
        {
            if (disabled) return "替换版 已停用";
            if (!enabled) return "替换版 校验中(" + verifyObjects + "对象)";
            return useRewrite ? "替换版 开(本轮" + takenOver + ")" : "替换版 关";
        }

        internal static string Summary()
        {
            return "[更好的存档] 序列化替换：模式 " + (disabled ? "已停用" : enabled ? "已接管" : "校验中")
                + (enabled && AbMode ? "（A/B 交替）" : "")
                + "，接管 " + takenOver + " 次，最近一次校验对象 " + verifyObjects + " 个，校验通过存档 "
                + verifiedSaves + " 次，不一致 " + mismatchObjects + " 个对象，异常回退 " + fallbacks
                + " 次，最大重入深度 " + maxDepth + "，类型表 " + Types.Count + " 项";
        }

        private static TypeInfo InfoOf(Type t)
        {
            if (ReferenceEquals(t, lastType)) return lastInfo;
            TypeInfo info;
            if (!Types.TryGetValue(t, out info))
            {
                info = new TypeInfo();
                info.Name = Encoding.UTF8.GetBytes(t.ToString());
                info.Skipped = t.IsDefined(skipAttr, false);
                Types[t] = info;
            }
            lastType = t;
            lastInfo = info;
            return info;
        }

        public static bool Prefix(SaveLoadRoot __instance, BinaryWriter __0)
        {
            depth++;
            if (depth > maxDepth) maxDepth = depth;

            if (depth != 1) return RunOriginal;
            if (disabled || !registryEmpty) return RunOriginal;
            if (ReferenceEquals(__instance, null) || __0 == null) return RunOriginal;

            PooledStream real = __0.BaseStream as PooledStream;

            if (enabled)
            {
                if (!useRewrite) return RunOriginal;
                if (real == null) return RunOriginal;
                long start = real.Position;
                try
                {
                    Rewrite(__instance, __0, real);
                    takenOver++;
                    return SkipOriginal;
                }
                catch (Exception e)
                {
                    real.SetLength(start);
                    real.Position = start;
                    disabled = true;
                    fallbacks++;
                    Debug.LogError("[更好的存档] 序列化替换版抛异常，本次已回退到原版并永久停用替换: " + e);
                    return RunOriginal;
                }
            }

            if (real == null) return RunOriginal;

            pending = false;
            try
            {
                scratch.SetLength(0);
                scratch.Position = 0;
                Rewrite(__instance, scratchWriter, scratch);
                pendingLen = (int)scratch.Length;
                pendingPos = real.Position;
                pending = true;
            }
            catch (Exception e)
            {
                pending = false;
                disabled = true;
                fallbacks++;
                Debug.LogError("[更好的存档] 序列化替换版校验期异常，永久停用替换: " + e);
            }
            return RunOriginal;
        }

        public static void After(BinaryWriter __0)
        {
            int d = depth;
            depth = d > 0 ? d - 1 : 0;
            if (d != 1 || !pending) return;
            pending = false;

            try
            {
                if (__0 == null) return;
                PooledStream real = __0.BaseStream as PooledStream;
                if (real == null) return;

                long end = real.Position;
                int len = (int)(end - pendingPos);
                if (len != pendingLen)
                {
                    Fail("长度 " + len + " != " + pendingLen);
                    return;
                }

                byte[] a = real.GetBuffer();
                byte[] b = scratch.GetBuffer();
                long p = pendingPos;
                for (int i = 0; i < len; i++)
                {
                    if (a[p + i] != b[i])
                    {
                        Fail("首个差异 @" + i + "（原版 " + a[p + i] + " / 替换版 " + b[i] + "）");
                        return;
                    }
                }
                verifyObjects++;
            }
            catch (Exception e)
            {
                FailSafe(e);
            }
        }

        private static void Fail(string detail)
        {
            mismatchObjects++;
            disabled = true;
            Debug.LogError("[更好的存档] 序列化替换版与原版输出不一致：" + detail
                + "，永久停用替换。校验期间真实数据始终由原版写出，未受影响");
        }

        private static void FailSafe(Exception e)
        {
            disabled = true;
            fallbacks++;
            Debug.LogError("[更好的存档] 序列化替换版校验过程异常，永久停用替换: " + e);
        }

        private static void Rewrite(SaveLoadRoot root, BinaryWriter w, PooledStream ps)
        {
            Component[] comps = root.GetComponents<KMonoBehaviour>();
            if (comps == null) return;

            int n = comps.Length;
            if (writeBuf.Length < n)
            {
                writeBuf = new bool[n];
                detailBuf = new ISaveLoadableDetails[n];
                infoBuf = new TypeInfo[n];
            }

            int count = 0;
            for (int i = 0; i < n; i++)
            {
                writeBuf[i] = false;
                Component c = comps[i];
                if (ReferenceEquals(c, null)) continue;
                TypeInfo info = InfoOf(c.GetType());
                if (info.Skipped) continue;
                infoBuf[i] = info;
                detailBuf[i] = c as ISaveLoadableDetails;
                writeBuf[i] = true;
                count++;
            }

            w.Write(count);

            for (int i = 0; i < n; i++)
            {
                if (!writeBuf[i]) continue;

                byte[] nb = infoBuf[i].Name;
                w.Write(nb.Length);
                w.Write(nb, 0, nb.Length);

                if (ps != null)
                {
                    int slot = (int)ps.Position;
                    w.Write(0);
                    long bodyStart = ps.Position;
                    WriteBody(comps[i], detailBuf[i], w);
                    int size = (int)(ps.Position - bodyStart);
                    byte[] buf = ps.GetBuffer();
                    buf[slot] = (byte)size;
                    buf[slot + 1] = (byte)(size >> 8);
                    buf[slot + 2] = (byte)(size >> 16);
                    buf[slot + 3] = (byte)(size >> 24);
                }
                else
                {
                    long slot = w.BaseStream.Position;
                    w.Write(0);
                    long bodyStart = w.BaseStream.Position;
                    WriteBody(comps[i], detailBuf[i], w);
                    long bodyEnd = w.BaseStream.Position;
                    int size = (int)(bodyEnd - bodyStart);
                    w.BaseStream.Position = slot;
                    w.Write(size);
                    w.BaseStream.Position = bodyEnd;
                }
            }
        }

        private static void WriteBody(Component c, ISaveLoadableDetails details, BinaryWriter w)
        {
            SerializerPatch.WriteTypeless(c, w);
            if (details != null) details.Serialize(w);
        }
    }
}
