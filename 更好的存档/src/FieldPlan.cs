using System;
using System.Collections.Generic;
using System.IO;
using Expr = System.Linq.Expressions.Expression;
using System.Reflection;
using HarmonyLib;
using KSerialization;
using UnityEngine;

namespace SaveOpt
{
    internal sealed class FieldPlan
    {
        internal FieldInfo Field;
        internal int Code;
        internal Func<object, object> Getter;
        internal Action<object, BinaryWriter> Fast;
        internal bool Checked;
        internal bool Trusted;
    }

    internal static class FieldPlanner
    {
        private const int ValueMask = 63;

        private static readonly MethodInfo SingleFast = AccessTools.Method(typeof(KSerialization.IOHelper), "WriteSingleFast");
        private static readonly MethodInfo KleiString = AccessTools.Method(typeof(KSerialization.IOHelper), "WriteKleiString");
        private static readonly Dictionary<FieldInfo, FieldPlan> Cache = new Dictionary<FieldInfo, FieldPlan>();

        private static readonly long[] FastByCode = new long[64];
        private static readonly long[] SlowByCode = new long[64];
        private static long fastCalls;
        private static long slowCalls;
        private static long propCalls;
        private static long checks;
        private static long rejections;

        internal static FieldPlan For(FieldInfo f, KSerialization.TypeInfo ti)
        {
            FieldPlan plan;
            if (Cache.TryGetValue(f, out plan)) return plan;
            plan = new FieldPlan();
            plan.Field = f;
            plan.Code = ti == null ? -1 : (((int)ti.info) & ValueMask);
            plan.Getter = Accessors.For(f);
            plan.Fast = Build(f, plan.Code);
            Cache[f] = plan;
            return plan;
        }

        internal static long Checks { get { return checks; } }
        internal static long Rejections { get { return rejections; } }

        internal static void NoteFast(int code)
        {
            fastCalls++;
            if (code >= 0 && code < 64) FastByCode[code]++;
        }

        internal static void NoteSlow(int code)
        {
            slowCalls++;
            if (code >= 0 && code < 64) SlowByCode[code]++;
        }

        internal static void NoteProperty()
        {
            propCalls++;
        }

        internal static void Check(FieldPlan plan, object obj, KSerialization.TypeInfo ti)
        {
            plan.Checked = true;
            checks++;
            try
            {
                byte[] fastBytes = Write(plan, obj);
                byte[] refBytes = Reference(plan, obj, ti);
                if (SameBytes(fastBytes, refBytes))
                {
                    plan.Trusted = true;
                    return;
                }
                plan.Fast = null;
                rejections++;
                if (rejections <= 10)
                {
                    Debug.LogError("[更好的存档] ★快速写入字节不一致，已永久回退原路径: "
                        + plan.Field.DeclaringType.Name + "." + plan.Field.Name + " code=" + plan.Code
                        + " 快速=" + fastBytes.Length + "B 参考=" + refBytes.Length + "B");
                }
            }
            catch (Exception e)
            {
                plan.Fast = null;
                rejections++;
                if (rejections <= 10)
                {
                    Debug.LogError("[更好的存档] ★快速写入异常，已永久回退原路径: "
                        + plan.Field.DeclaringType.Name + "." + plan.Field.Name + " : " + e.Message);
                }
            }
        }

        private static byte[] Write(FieldPlan plan, object obj)
        {
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                plan.Fast(obj, w);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] Reference(FieldPlan plan, object obj, KSerialization.TypeInfo ti)
        {
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                KSerialization.Helper.WriteValue(w, ti, plan.Getter(obj));
                w.Flush();
                return ms.ToArray();
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static Action<object, BinaryWriter> Build(FieldInfo f, int code)
        {
            try
            {
                Type ft = f.FieldType;
                var objP = Expr.Parameter(typeof(object), "o");
                var wP = Expr.Parameter(typeof(BinaryWriter), "w");
                Expr inst = f.DeclaringType.IsValueType
                    ? (Expr)Expr.Unbox(objP, f.DeclaringType)
                    : Expr.Convert(objP, f.DeclaringType);
                Expr fld = Expr.Field(inst, f);
                Expr body;

                switch (code)
                {
                    case 1:
                        if (ft != typeof(sbyte)) return null;
                        body = Call(wP, typeof(sbyte), fld);
                        break;
                    case 2:
                        if (ft != typeof(byte)) return null;
                        body = Call(wP, typeof(byte), fld);
                        break;
                    case 3:
                        if (ft != typeof(bool)) return null;
                        body = Call(wP, typeof(byte), Expr.Condition(fld, Expr.Constant((byte)1), Expr.Constant((byte)0)));
                        break;
                    case 4:
                        if (ft != typeof(short)) return null;
                        body = Call(wP, typeof(short), fld);
                        break;
                    case 5:
                        if (ft != typeof(ushort)) return null;
                        body = Call(wP, typeof(ushort), fld);
                        break;
                    case 6:
                        if (ft != typeof(int)) return null;
                        body = Call(wP, typeof(int), fld);
                        break;
                    case 7:
                        if (ft != typeof(uint)) return null;
                        body = Call(wP, typeof(uint), fld);
                        break;
                    case 8:
                        if (ft != typeof(long)) return null;
                        body = Call(wP, typeof(long), fld);
                        break;
                    case 9:
                        if (ft != typeof(ulong)) return null;
                        body = Call(wP, typeof(ulong), fld);
                        break;
                    case 10:
                        if (ft != typeof(float)) return null;
                        body = Expr.Call(SingleFast, wP, fld);
                        break;
                    case 11:
                        if (ft != typeof(double)) return null;
                        body = Call(wP, typeof(double), fld);
                        break;
                    case 12:
                        if (ft != typeof(string)) return null;
                        body = Expr.Call(KleiString, wP, fld);
                        break;
                    case 13:
                        if (!ft.IsEnum || Enum.GetUnderlyingType(ft) != typeof(int)) return null;
                        body = Call(wP, typeof(int), Expr.Convert(fld, typeof(int)));
                        break;
                    case 14:
                        if (ft != typeof(Vector2I)) return null;
                        body = Expr.Block(
                            Call(wP, typeof(int), Expr.Field(fld, "x")),
                            Call(wP, typeof(int), Expr.Field(fld, "y")));
                        break;
                    case 15:
                        if (ft != typeof(Vector2)) return null;
                        body = Expr.Block(
                            Expr.Call(SingleFast, wP, Expr.Field(fld, "x")),
                            Expr.Call(SingleFast, wP, Expr.Field(fld, "y")));
                        break;
                    case 16:
                        if (ft != typeof(Vector3)) return null;
                        body = Expr.Block(
                            Expr.Call(SingleFast, wP, Expr.Field(fld, "x")),
                            Expr.Call(SingleFast, wP, Expr.Field(fld, "y")),
                            Expr.Call(SingleFast, wP, Expr.Field(fld, "z")));
                        break;
                    case 23:
                        if (ft != typeof(Color)) return null;
                        body = Expr.Block(
                            Call(wP, typeof(byte), ToByte(Expr.Field(fld, "r"))),
                            Call(wP, typeof(byte), ToByte(Expr.Field(fld, "g"))),
                            Call(wP, typeof(byte), ToByte(Expr.Field(fld, "b"))),
                            Call(wP, typeof(byte), ToByte(Expr.Field(fld, "a"))));
                        break;
                    default:
                        return null;
                }

                return Expr.Lambda<Action<object, BinaryWriter>>(body, objP, wP).Compile();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 快速写入编译失败，回退原路径: " + f.DeclaringType.Name + "." + f.Name + " : " + e.Message);
                return null;
            }
        }

        private static Expr ToByte(Expr floatExpr)
        {
            return Expr.Convert(Expr.Multiply(floatExpr, Expr.Constant(255f)), typeof(byte));
        }

        private static Expr Call(Expr writer, Type argType, Expr arg)
        {
            MethodInfo m = typeof(BinaryWriter).GetMethod("Write", new[] { argType });
            if (m == null) throw new MissingMethodException("BinaryWriter.Write(" + argType.Name + ")");
            return Expr.Call(writer, m, arg);
        }

        internal static string Summary()
        {
            long total = fastCalls + slowCalls;
            var sb = new System.Text.StringBuilder("[更好的存档] 第五刀：快速写入 " + fastCalls + " 次");
            if (total > 0)
            {
                sb.Append("（占 ").Append((fastCalls * 100.0 / total).ToString("F1")).Append("%）");
            }
            sb.Append("，回退 ").Append(slowCalls).Append(" 次，属性 ").Append(propCalls)
              .Append(" 次；字段字节校验 ").Append(checks).Append(" 个，拒绝 ").Append(rejections).Append(" 个");

            sb.Append(" ｜ 快写: ");
            AppendCodes(sb, FastByCode, true);
            sb.Append(" ｜ 回退: ");
            AppendCodes(sb, SlowByCode, false);
            return sb.ToString();
        }

        private static void AppendCodes(System.Text.StringBuilder sb, long[] counts, bool skipZero)
        {
            bool any = false;
            for (int c = 0; c < 64; c++)
            {
                if (skipZero && counts[c] == 0) continue;
                if (any) sb.Append(", ");
                sb.Append(CodeName(c)).Append('=').Append(counts[c]);
                any = true;
            }
            if (!any) sb.Append("无");
        }

        private static string CodeName(int c)
        {
            switch (c)
            {
                case 0: return "用户定义";
                case 1: return "SByte";
                case 2: return "Byte";
                case 3: return "Bool";
                case 4: return "Int16";
                case 5: return "UInt16";
                case 6: return "Int32";
                case 7: return "UInt32";
                case 8: return "Int64";
                case 9: return "UInt64";
                case 10: return "Single";
                case 11: return "Double";
                case 12: return "String";
                case 13: return "枚举";
                case 14: return "Vector2I";
                case 15: return "Vector2";
                case 16: return "Vector3";
                case 17: return "Array";
                case 18: return "Pair";
                case 19: return "Dict";
                case 20: return "List";
                case 21: return "HashSet";
                case 22: return "Queue";
                case 23: return "Color";
                default: return "码" + c;
            }
        }
    }
}
