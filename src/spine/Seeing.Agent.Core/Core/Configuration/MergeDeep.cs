using System.Collections;
using System.Reflection;

namespace Seeing.Agent.Core.Configuration
{
    /// <summary>
    /// 深度合并工具 - 用于层级配置合并
    /// <para>
    /// 合并规则：
    /// - 原始类型：覆盖值生效（非默认值时）；bool false 例外，视为合法覆盖
    /// - 可空类型：null 表示未设置；持有默认值（如 0）同样视为未设置
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
            // 单侧为 null 时返回深拷贝：避免合并结果与来源快照共享引用而被后续修改污染。
            if (@base == null)
                return @override == null ? new T() : (T)CloneValue(@override)!;
            if (@override == null)
                return (T)CloneValue(@base)!;

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
            // null 分支返回深拷贝：避免合并结果与来源（用户级/项目级快照）共享引用而被后续修改污染。
            if (overrideValue == null)
                return CloneValue(baseValue);

            // 如果基础值为 null，使用覆盖值的深拷贝
            if (baseValue == null)
                return CloneValue(overrideValue);

            // 处理原始类型和字符串
            if (IsPrimitiveType(type))
            {
                // bool 的 false 是合法覆盖值（如 Acp.Enabled），不能当作“未设置”
                if (type == typeof(bool))
                    return overrideValue;

                // 如果覆盖值是默认值，使用基础值
                if (IsDefault(overrideValue, type))
                    return baseValue;
                return overrideValue;
            }

            // 处理可空类型
            // 反序列化无法区分“未写”与“写 0”，故默认值（如 0）视为未设置，保留基础值。
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

            try
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MergeDeep: 无法创建类型 {type?.FullName} 的默认实例: {ex.Message}");
                return null;
            }
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

        /// <summary>
        /// 深拷贝任意值：用于消除合并结果与配置来源快照之间的引用共享。
        /// <para>不可变标量直接返回；数组/字典/集合逐元素拷贝；复杂对象逐可写属性递归拷贝。</para>
        /// </summary>
        private static object? CloneValue(object? value)
        {
            if (value == null) return null;

            var type = value.GetType();

            // 不可变标量：装箱值类型本身即副本，字符串不可变，直接返回
            if (type.IsPrimitive ||
                type.IsEnum ||
                type == typeof(string) ||
                type == typeof(decimal) ||
                type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) ||
                type == typeof(TimeSpan) ||
                type == typeof(Guid))
            {
                return value;
            }

            // 数组
            if (value is Array array)
            {
                var elementType = type.GetElementType()!;
                var clone = Array.CreateInstance(elementType, array.Length);
                for (var i = 0; i < array.Length; i++)
                    clone.SetValue(CloneValue(array.GetValue(i)), i);
                return clone;
            }

            // 字典
            if (value is IDictionary dictionary)
            {
                if (Activator.CreateInstance(type) is not IDictionary clone)
                    return value;

                foreach (DictionaryEntry entry in dictionary)
                    clone[entry.Key] = CloneValue(entry.Value);
                return clone;
            }

            // 列表/集合
            if (value is IList list)
            {
                if (Activator.CreateInstance(type) is not IList clone)
                    return value;

                foreach (var item in list)
                    clone.Add(CloneValue(item));
                return clone;
            }

            // 复杂对象：逐可写属性递归拷贝
            if (IsComplexObject(type))
            {
                object? clone;
                try
                {
                    clone = Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MergeDeep: 无法克隆类型 {type.FullName} 的实例: {ex.Message}");
                    return value;
                }

                if (clone == null)
                    return value;

                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanRead || !property.CanWrite)
                        continue;

                    property.SetValue(clone, CloneValue(property.GetValue(value)));
                }

                return clone;
            }

            return value;
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
            IDictionary? result;
            try
            {
                result = Activator.CreateInstance(type) as IDictionary;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MergeDeep: 无法创建字典类型 {type?.FullName} 的实例: {ex.Message}");
                return overrideValue;
            }

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
                    if (entry.Value == null)
                        continue;

                    if (result.Contains(entry.Key) && result[entry.Key] != null)
                    {
                        var valueType = entry.Value.GetType();
                        if (IsComplexObject(valueType))
                        {
                            result[entry.Key] = MergeObject(result[entry.Key]!, entry.Value, valueType);
                            continue;
                        }
                    }

                    result[entry.Key] = entry.Value;
                }
            }

            return result;
        }

        private static object? MergeObject(object baseValue, object overrideValue, Type type)
        {
            object? result;
            try
            {
                result = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MergeDeep: 无法创建对象类型 {type?.FullName} 的实例: {ex.Message}");
                return overrideValue;
            }

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