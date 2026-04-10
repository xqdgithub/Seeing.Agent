using System.Collections;
using System.Reflection;

namespace Seeing.Agent.Core.Configuration
{
    /// <summary>
    /// 深度合并工具 - 用于层级配置合并
    /// <para>
    /// 合并规则：
    /// - 原始类型：覆盖值生效（非默认值时）
    /// - 数组：覆盖值替换（非合并）
    /// - 对象：递归属性合并
    /// - 字典：合并键，冲突时覆盖值生效
    /// </para>
    /// </summary>
    public static class MergeDeep
    {
        /// <summary>
        /// 深度合并两个对象
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        /// <param name="base">基础对象</param>
        /// <param name="override">覆盖对象</param>
        /// <returns>合并后的新对象</returns>
        public static T Merge<T>(T? @base, T? @override) where T : new()
        {
            if (@base == null) return @override ?? new T();
            if (@override == null) return @base;

            var result = new T();
            var type = typeof(T);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;

                var baseValue = property.GetValue(@base);
                var overrideValue = property.GetValue(@override);

                var mergedValue = MergeValues(baseValue, overrideValue, property.PropertyType);
                property.SetValue(result, mergedValue);
            }

            return result;
        }

        /// <summary>
        /// 合并多个对象（按顺序合并）
        /// </summary>
        public static T MergeChain<T>(params T?[] sources) where T : new()
        {
            if (sources == null || sources.Length == 0)
                return new T();

            T? result = default;
            
            foreach (var source in sources)
            {
                if (source == null) continue;
                result = Merge(result ?? new T(), source);
            }

            return result ?? new T();
        }

        private static object? MergeValues(object? baseValue, object? overrideValue, Type type)
        {
            // 如果覆盖值为 null，使用基础值
            if (overrideValue == null)
                return baseValue;

            // 如果基础值为 null，使用覆盖值
            if (baseValue == null)
                return overrideValue;

            // 处理原始类型和字符串
            if (IsPrimitiveType(type))
            {
                // 如果覆盖值是默认值，使用基础值
                if (IsDefault(overrideValue, type))
                    return baseValue;
                return overrideValue;
            }

            // 处理可空类型
            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                if (IsDefault(overrideValue, underlyingType))
                    return baseValue;
                return overrideValue;
            }

            // 处理字典
            if (IsDictionary(type))
                return MergeDictionary(baseValue, overrideValue, type);

            // 处理集合/数组：覆盖值替换（非默认时）
            if (IsCollection(type))
            {
                if (IsEmptyCollection(overrideValue))
                    return baseValue;
                return overrideValue;
            }

            // 处理复杂对象：递归合并
            if (IsComplexObject(type))
                return MergeObject(baseValue, overrideValue, type);

            // 默认：覆盖值生效
            return overrideValue;
        }

        private static bool IsDefault(object? value, Type type)
        {
            if (value == null) return true;
            
            var defaultValue = GetDefault(type);
            return Equals(value, defaultValue);
        }

        private static object? GetDefault(Type type)
        {
            // string 的默认值是空字符串，不是 null
            if (type == typeof(string))
                return string.Empty;
            
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static bool IsEmptyCollection(object? value)
        {
            if (value == null) return true;
            if (value is ICollection collection)
                return collection.Count == 0;
            return false;
        }

        private static bool IsPrimitiveType(Type type)
        {
            return type.IsPrimitive ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(Guid);
        }

        private static bool IsDictionary(Type type)
        {
            return typeof(IDictionary).IsAssignableFrom(type) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>));
        }

        private static bool IsCollection(Type type)
        {
            return typeof(ICollection).IsAssignableFrom(type) ||
                   type.IsArray ||
                   (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type));
        }

        private static bool IsComplexObject(Type type)
        {
            return !type.IsPrimitive && 
                   type != typeof(string) && 
                   !type.IsArray && 
                   typeof(IEnumerable).IsAssignableFrom(type) == false &&
                   !type.IsValueType;
        }

        private static object? MergeDictionary(object baseValue, object overrideValue, Type type)
        {
            var result = Activator.CreateInstance(type) as IDictionary;
            if (result == null) return overrideValue;

            // 添加基础字典的所有键
            if (baseValue is IDictionary baseDict)
            {
                foreach (DictionaryEntry entry in baseDict)
                {
                    result[entry.Key] = entry.Value;
                }
            }

            // 合并覆盖字典的键
            if (overrideValue is IDictionary overrideDict)
            {
                foreach (DictionaryEntry entry in overrideDict)
                {
                    result[entry.Key] = entry.Value;
                }
            }

            return result;
        }

        private static object? MergeObject(object baseValue, object overrideValue, Type type)
        {
            var result = Activator.CreateInstance(type);
            if (result == null) return overrideValue;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;

                var basePropValue = property.GetValue(baseValue);
                var overridePropValue = property.GetValue(overrideValue);

                var mergedValue = MergeValues(basePropValue, overridePropValue, property.PropertyType);
                property.SetValue(result, mergedValue);
            }

            return result;
        }
    }
}