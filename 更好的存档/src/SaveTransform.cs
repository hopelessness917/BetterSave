using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SaveOpt
{
    internal static class SaveTransform
    {
        private const int MinVerifiedObjects = 20000;

        private static Type skipAttr;
        private static FieldInfo managersField;
        private static bool registryEmpty;
        private static bool enabled;
        private static bool disabled;
        private static bool warnedRegistry;
        private static long takenOver;
        private static long verifyObjects;
        private static long mismatchObjects;
        private static long mismatchSaves;
        private static long fallbacks;
        private static int verifiedSaves;

        private static bool pending;
        private static long pendingPos;
        private static int pendingLen;

        private static readonly byte[] ScratchBuffer = new byte[1048576];
        private static PooledStream scratch;
        private static BinaryWriter scratchWriter;

        private static Type[] typeBuf = new Type[64];
        private static bool[] writeBuf = new bool[64];
        private static ISaveLoadableDetails[] detailBuf = new ISaveLoadableDetails[64];

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
            Debug.Log("[更好的存档] 序列化替换已挂载（首次存档逐字节校验通过后接管）");
            return true;
        }

        internal static void BeginSave()
        {
            mismatchObjects = 0;
            pending = false;

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
            if (disabled || enabled)
            {
                pending = false;
                return;
            }
            if (mismatchObjects == 0 && verifyObjects > 0)
            {
                verifiedSaves++;
                if (verifyObjects >= MinVerifiedObjects || verifiedSaves >= 3)
                {
                    enabled = true;
                    Debug.Log("[更好的存档] 序列化替换版已通过逐字节校验（本次存档 " + verifyObjects
                        + " 个对象，累计 " + verifiedSaves + " 次），下次存档起接管主线程序列化");
                }
            }
            verifyObjects = 0;
        }

        internal static string Status()
        {
            if (disabled) return "替换版 已停用";
            if (enabled) return "替换版 已接管(" + takenOver + ")";
            return "替换版 校验中(" + verifyObjects + "对象)";
        }

        internal static string Summary()
        {
            return "[更好的存档] 序列化替换：模式 " + (disabled ? "已停用" : enabled ? "已接管" : "校验中")
                + "，接管 " + takenOver + " 次，校验对象 " + verifyObjects + " 个，校验存档 " + verifiedSaves
                + " 次，不一致 " + mismatchObjects + " 个对象/" + mismatchSaves + " 次存档，异常回退 " + fallbacks + " 次";
        }

        public static bool Prefix(SaveLoadRoot __instance, BinaryWriter __0)
        {
            SaveLoadRoot root = __instance;
            BinaryWriter writer = __0;
            pending = false;
            try
            {
                if (disabled || !registryEmpty) return false;
                if (ReferenceEquals(root, null) || writer == null) return false;

                PooledStream real = writer.BaseStream as PooledStream;

                if (enabled)
                {
                    long start = real == null ? 0 : real.Position;
                    try
                    {
                        Rewrite(root, writer, real);
                        takenOver++;
                        return true;
                    }
                    catch (Exception e)
                    {
                        if (real != null)
                        {
                            real.SetLength(start);
                            real.Position = start;
                        }
                        disabled = true;
                        fallbacks++;
                        Debug.LogError("[更好的存档] 序列化替换版抛异常，本次已回退到原版并永久停用替换: " + e);
                        return false;
                    }
                }

                if (real == null) return false;

                scratch.SetLength(0);
                scratch.Position = 0;
                Rewrite(root, scratchWriter, scratch);
                pendingLen = (int)scratch.Length;
                pendingPos = real.Position;
                pending = true;
                return false;
            }
            catch (Exception e)
            {
                pending = false;
                disabled = true;
                fallbacks++;
                Debug.LogError("[更好的存档] 序列化替换版校验期异常，永久停用替换: " + e);
                return false;
            }
        }

        public static void After(BinaryWriter __0)
        {
            if (!pending) return;
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
            mismatchSaves++;
            disabled = true;
            Debug.LogError("[更好的存档] 序列化替换版与原版输出不一致：" + detail
                + "，永久停用替换（本次存档仍由原版写出，安全）");
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
            if (typeBuf.Length < n)
            {
                typeBuf = new Type[n];
                writeBuf = new bool[n];
                detailBuf = new ISaveLoadableDetails[n];
            }

            int count = 0;
            for (int i = 0; i < n; i++)
            {
                writeBuf[i] = false;
                Component c = comps[i];
                if (ReferenceEquals(c, null)) continue;
                Type t = c.GetType();
                if (IsDefinedCache.Check(t, skipAttr, false)) continue;
                typeBuf[i] = t;
                detailBuf[i] = c as ISaveLoadableDetails;
                writeBuf[i] = true;
                count++;
            }

            w.Write(count);

            for (int i = 0; i < n; i++)
            {
                if (!writeBuf[i]) continue;
                KSerialization.IOHelper.WriteKleiString(w, typeBuf[i].ToString());

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
            if (details != null)
            {
                KSerialization.Serializer.SerializeTypeless(c, w);
                details.Serialize(w);
            }
            else if (!ReferenceEquals(c, null))
            {
                KSerialization.Serializer.SerializeTypeless(c, w);
            }
        }
    }
}
