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
        internal int Mask;
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

        private static long checks;
        private static long rejections;

        internal static FieldPlan For(FieldInfo f, KSerialization.TypeInfo ti, int mask)
        {
            FieldPlan plan;
            if (Cache.TryGetValue(f, out plan) && plan.Mask == mask) return plan;

            int code = ti == null ? -1 : (((int)ti.info) & ValueMask);
            if (plan == null)
            {
                plan = new FieldPlan();
                plan.Field = f;
                plan.Getter = Accessors.For(f);
                Cache[f] = plan;
            }
            plan.Mask = mask;
            plan.Code = code;
            plan.Checked = false;
            plan.Trusted = false;
            plan.Fast = Build(f, code);
            return plan;
        }

        internal static long Checks { get { return checks; } }
        internal static long Rejections { get { return rejections; } }

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
                var objP = Expr.Parameter(typeof(object), "o");
                var wP = Expr.Parameter(typeof(BinaryWriter), "w");
                Expr body = BuildExpr(f, code, objP, wP);
                if (body == null) return null;
                return Expr.Lambda<Action<object, BinaryWriter>>(body, objP, wP).Compile();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 快速写入编译失败，回退原路径: " + f.DeclaringType.Name + "." + f.Name + " : " + e.Message);
                return null;
            }
        }

        internal static Expr BuildExpr(FieldInfo f, int code, Expr objP, Expr wP)
        {
            Type ft = f.FieldType;
            Expr inst = f.DeclaringType.IsValueType
                ? (Expr)Expr.Unbox(objP, f.DeclaringType)
                : Expr.Convert(objP, f.DeclaringType);
            Expr fld = Expr.Field(inst, f);

            switch (code)
            {
                case 1:
                    if (ft != typeof(sbyte)) return null;
                    return Call(wP, typeof(sbyte), fld);
                case 2:
                    if (ft != typeof(byte)) return null;
                    return Call(wP, typeof(byte), fld);
                case 3:
                    if (ft != typeof(bool)) return null;
                    return Call(wP, typeof(byte), Expr.Condition(fld, Expr.Constant((byte)1), Expr.Constant((byte)0)));
                case 4:
                    if (ft != typeof(short)) return null;
                    return Call(wP, typeof(short), fld);
                case 5:
                    if (ft != typeof(ushort)) return null;
                    return Call(wP, typeof(ushort), fld);
                case 6:
                    if (ft != typeof(int)) return null;
                    return Call(wP, typeof(int), fld);
                case 7:
                    if (ft != typeof(uint)) return null;
                    return Call(wP, typeof(uint), fld);
                case 8:
                    if (ft != typeof(long)) return null;
                    return Call(wP, typeof(long), fld);
                case 9:
                    if (ft != typeof(ulong)) return null;
                    return Call(wP, typeof(ulong), fld);
                case 10:
                    if (ft != typeof(float)) return null;
                    return Expr.Call(SingleFast, wP, fld);
                case 11:
                    if (ft != typeof(double)) return null;
                    return Call(wP, typeof(double), fld);
                case 12:
                    if (ft != typeof(string)) return null;
                    return Expr.Call(KleiString, wP, fld);
                case 13:
                    if (!ft.IsEnum || Enum.GetUnderlyingType(ft) != typeof(int)) return null;
                    return Call(wP, typeof(int), Expr.Convert(fld, typeof(int)));
                case 14:
                    if (ft != typeof(Vector2I)) return null;
                    return Expr.Block(
                        Call(wP, typeof(int), Expr.Field(fld, "x")),
                        Call(wP, typeof(int), Expr.Field(fld, "y")));
                case 15:
                    if (ft != typeof(Vector2)) return null;
                    return Expr.Block(
                        Expr.Call(SingleFast, wP, Expr.Field(fld, "x")),
                        Expr.Call(SingleFast, wP, Expr.Field(fld, "y")));
                case 16:
                    if (ft != typeof(Vector3)) return null;
                    return Expr.Block(
                        Expr.Call(SingleFast, wP, Expr.Field(fld, "x")),
                        Expr.Call(SingleFast, wP, Expr.Field(fld, "y")),
                        Expr.Call(SingleFast, wP, Expr.Field(fld, "z")));
                case 23:
                    if (ft != typeof(Color)) return null;
                    return Expr.Block(
                        Call(wP, typeof(byte), ToByte(Expr.Field(fld, "r"))),
                        Call(wP, typeof(byte), ToByte(Expr.Field(fld, "g"))),
                        Call(wP, typeof(byte), ToByte(Expr.Field(fld, "b"))),
                        Call(wP, typeof(byte), ToByte(Expr.Field(fld, "a"))));
                default:
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
            return "[更好的存档] 字段快写：缓存 " + Cache.Count + " 个字段，字节校验 "
                + checks + " 个，拒绝 " + rejections + " 个";
        }
    }
}
