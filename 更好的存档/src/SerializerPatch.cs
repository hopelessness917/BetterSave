using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SaveOpt
{
    internal static class Accessors
    {
        private static readonly Dictionary<FieldInfo, Func<object, object>> FieldCache = new Dictionary<FieldInfo, Func<object, object>>();
        private static readonly Dictionary<PropertyInfo, Func<object, object>> PropCache = new Dictionary<PropertyInfo, Func<object, object>>();

        private static long compiled;
        private static long fallback;

        internal static long Compiled { get { return compiled; } }
        internal static long Fallback { get { return fallback; } }

        internal static Func<object, object> For(FieldInfo f)
        {
            Func<object, object> get;
            if (FieldCache.TryGetValue(f, out get)) return get;
            get = BuildField(f);
            FieldCache[f] = get;
            return get;
        }

        internal static Func<object, object> For(PropertyInfo p)
        {
            Func<object, object> get;
            if (PropCache.TryGetValue(p, out get)) return get;
            get = BuildProperty(p);
            PropCache[p] = get;
            return get;
        }

        private static Func<object, object> BuildField(FieldInfo f)
        {
            try
            {
                var p = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                System.Linq.Expressions.Expression inst = f.DeclaringType.IsValueType
                    ? (System.Linq.Expressions.Expression)System.Linq.Expressions.Expression.Unbox(p, f.DeclaringType)
                    : System.Linq.Expressions.Expression.Convert(p, f.DeclaringType);
                var body = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(inst, f), typeof(object));
                var d = System.Linq.Expressions.Expression.Lambda<Func<object, object>>(body, p).Compile();
                compiled++;
                return d;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 字段访问器编译失败，回退反射: " + f.DeclaringType.Name + "." + f.Name + " : " + e.Message);
                fallback++;
                return o => f.GetValue(o);
            }
        }

        private static Func<object, object> BuildProperty(PropertyInfo p)
        {
            try
            {
                var par = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                var body = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(par, p.DeclaringType), p), typeof(object));
                var d = System.Linq.Expressions.Expression.Lambda<Func<object, object>>(body, par).Compile();
                compiled++;
                return d;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 属性访问器编译失败，回退反射: " + p.DeclaringType.Name + "." + p.Name + " : " + e.Message);
                fallback++;
                return o => p.GetValue(o, null);
            }
        }
    }

    internal static class SerializerPatch
    {
        private static volatile bool verify = true;
        private static long verified;
        private static long mismatches;
        private static long calls;
        private static long directCalls;

        private sealed class TemplatePlan
        {
            internal KSerialization.SerializationTemplate Template;
            internal FieldPlan[] Plans;
            internal KSerialization.TypeInfo[] TypeInfos;
        }

        private static readonly Dictionary<Type, TemplatePlan> ByType = new Dictionary<Type, TemplatePlan>();
        private static readonly Dictionary<KSerialization.SerializationTemplate, TemplatePlan> ByTemplate =
            new Dictionary<KSerialization.SerializationTemplate, TemplatePlan>();

        private static int currentMask = -1;

        internal static void ClearPlans()
        {
            ByType.Clear();
            ByTemplate.Clear();
        }

        internal static bool VerifyMode { get { return verify; } }
        internal static long Verified { get { return verified; } }
        internal static long Mismatches { get { return mismatches; } }
        internal static long Calls { get { return calls; } }
        internal static long DirectCalls { get { return directCalls; } }

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(KSerialization.SerializationTemplate), "SerializeData");
            if (target == null)
            {
                Debug.LogError("[更好的存档] 找不到 SerializationTemplate.SerializeData，第二刀未挂载");
                return false;
            }
            harmony.Patch(target, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SerializerPatch), "Prefix")));
            PatchMask(harmony, "SetTypeInfoMask");
            PatchMask(harmony, "ClearTypeInfoMask");
            Diag.Trace("[更好的存档] SerializationTemplate.SerializeData 已接管（编译委托替代逐字段反射，首轮存档做双路校验）");
            return true;
        }

        private static void PatchMask(HarmonyLib.Harmony harmony, string name)
        {
            try
            {
                int hit = 0;
                bool isSet = name == "SetTypeInfoMask";
                foreach (MethodInfo m in typeof(KSerialization.Helper).GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != name) continue;
                    harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SerializerPatch),
                        isSet ? "SetMask_Prefix" : "ClearMask_Prefix")));
                    hit++;
                }
                if (hit == 0) Debug.LogWarning("[更好的存档] 找不到 Helper." + name + "，掩码变化时字段计划不会重建");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] Helper." + name + " 挂载失败: " + e.Message);
            }
        }

        public static void SetMask_Prefix(int __0)
        {
            currentMask = __0;
        }

        public static void ClearMask_Prefix()
        {
            currentMask = -1;
        }

        internal static void EndVerify(string reason)
        {
            if (!verify) return;
            verify = false;
            Debug.Log("[更好的存档] 双路校验结束（" + reason + "）：回退路径比对 " + verified + " 次字段读取，不一致 " + mismatches
                + " 次（快写路径另有 " + FieldPlanner.Checks + " 个字段做过字节级比对，拒绝 " + FieldPlanner.Rejections + " 个）");
        }

        public static bool Prefix(KSerialization.SerializationTemplate __instance, object obj, System.IO.BinaryWriter writer)
        {
            calls++;
            Run(PlanFor(__instance), obj, writer);
            return false;
        }

        private static TemplatePlan PlanFor(KSerialization.SerializationTemplate t)
        {
            if (t == null) return new TemplatePlan();

            TemplatePlan tp;
            if (ByTemplate.TryGetValue(t, out tp)) return tp;

            tp = new TemplatePlan();
            tp.Template = t;

            var fields = t.serializableFields;
            int n = fields == null ? 0 : fields.Count;
            tp.Plans = new FieldPlan[n];
            tp.TypeInfos = new KSerialization.TypeInfo[n];
            for (int i = 0; i < n; i++)
            {
                tp.TypeInfos[i] = fields[i].typeInfo;
                tp.Plans[i] = FieldPlanner.For(fields[i].field, fields[i].typeInfo, currentMask);
            }

            ByTemplate[t] = tp;
            if (t.serializableType != null) ByType[t.serializableType] = tp;
            return tp;
        }

        internal static void SlowField(FieldPlan plan, KSerialization.TypeInfo ti, object obj, BinaryWriter writer)
        {
            if (plan.Fast != null)
            {
                if (!plan.Checked) FieldPlanner.Check(plan, obj, ti);
                if (plan.Trusted)
                {
                    plan.Fast(obj, writer);
                    return;
                }
            }
            object value = plan.Getter(obj);
            if (verify) value = VerifyField(plan.Field, obj, value);
            KSerialization.Helper.WriteValue(writer, ti, value);
        }

        internal static void WriteTypeless(object obj, BinaryWriter writer)
        {
            directCalls++;
            Type type = obj.GetType();
            TemplatePlan tp;
            if (!ByType.TryGetValue(type, out tp))
            {
                tp = PlanFor(KSerialization.Manager.GetSerializationTemplate(type));
                ByType[type] = tp;
            }
            Run(tp, obj, writer);
        }

        private static void Run(TemplatePlan tp, object obj, System.IO.BinaryWriter writer)
        {
            KSerialization.SerializationTemplate __instance = tp.Template;
            if (__instance.onSerializing != null) __instance.onSerializing.Invoke(obj, null);

            bool check = verify;
            FieldPlan[] plans = tp.Plans;
            if (plans != null && plans.Length > 0)
            {
                for (int i = 0; i < plans.Length; i++)
                {
                    try
                    {
                        SlowField(plans[i], tp.TypeInfos[i], obj, writer);
                    }
                    catch (Exception inner)
                    {
                        string text = string.Format("Error occurred while serializing field {0} on template {1}",
                            plans[i].Field.Name, __instance.serializableType.Name);
                        Debug.LogError(text);
                        throw new ArgumentException(text, inner);
                    }
                }
            }

            var props = __instance.serializableProperties;
            if (props != null)
            {
                for (int i = 0; i < props.Count; i++)
                {
                    KSerialization.SerializationTemplate.SerializationProperty sp = props[i];
                    try
                    {
                        object value2 = Accessors.For(sp.property)(obj);
                        if (check) value2 = VerifyProperty(sp.property, obj, value2);
                        KSerialization.Helper.WriteValue(writer, sp.typeInfo, value2);
                    }
                    catch (Exception inner2)
                    {
                        string text2 = string.Format("Error occurred while serializing property {0} on template {1}", sp.property.Name, __instance.serializableType.Name);
                        Debug.LogError(text2);
                        throw new ArgumentException(text2, inner2);
                    }
                }
            }

            if (__instance.customSerialize != null) __instance.customSerialize.Invoke(obj, new object[] { writer });
            if (__instance.onSerialized != null) __instance.onSerialized.Invoke(obj, null);
        }

        private static object VerifyField(FieldInfo f, object obj, object fromDelegate)
        {
            verified++;
            try
            {
                object expected = f.GetValue(obj);
                if (!Equals(expected, fromDelegate))
                {
                    mismatches++;
                    if (mismatches <= 10)
                        Debug.LogError("[更好的存档] ★字段读取不一致，已改用反射值以保证存档正确: " + f.DeclaringType.Name + "." + f.Name
                            + " 反射=[" + (expected == null ? "null" : expected.ToString()) + "] 委托=[" + (fromDelegate == null ? "null" : fromDelegate.ToString()) + "]");
                    return expected;
                }
            }
            catch (Exception e)
            {
                mismatches++;
                if (mismatches <= 10) Debug.LogError("[更好的存档] ★校验异常，已改用反射值: " + f.Name + " : " + e.Message);
                try { return f.GetValue(obj); } catch { }
            }
            return fromDelegate;
        }

        private static object VerifyProperty(PropertyInfo p, object obj, object fromDelegate)
        {
            verified++;
            try
            {
                object expected = p.GetValue(obj, null);
                if (!Equals(expected, fromDelegate))
                {
                    mismatches++;
                    if (mismatches <= 10)
                        Debug.LogError("[更好的存档] ★属性读取不一致，已改用反射值: " + p.DeclaringType.Name + "." + p.Name);
                    return expected;
                }
            }
            catch (Exception e)
            {
                mismatches++;
                if (mismatches <= 10) Debug.LogError("[更好的存档] ★属性校验异常，已改用反射值: " + p.Name + " : " + e.Message);
            }
            return fromDelegate;
        }

        internal static string Summary()
        {
            return "[更好的存档] 第二刀：SerializeData 调用 " + calls + " 次（其中直调 " + directCalls
                + " 次，绕开 Harmony 跳板）；委托编译 " + Accessors.Compiled
                + " 个，回退反射 " + Accessors.Fallback + " 个；双路比对 " + verified + " 次，不一致 " + mismatches + " 次"
                + (verify ? "（校验仍在进行）" : "（校验已结束）");
        }
    }
}