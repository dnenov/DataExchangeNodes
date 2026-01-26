using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Shared reflection utilities for DataExchange operations.
    /// Provides cached type/method lookups and response handling for SDK interactions.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal static class ReflectionUtils
    {
        // Static caches for deterministic lookups (thread-safe for read-heavy usage)
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private static readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> _propertyCache = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, ConstructorInfo> _constructorCache = new Dictionary<string, ConstructorInfo>();

        /// <summary>
        /// Gets a method from a type using reflection with optional parameter type matching.
        /// </summary>
        /// <param name="type">The type to search</param>
        /// <param name="methodName">The method name</param>
        /// <param name="flags">Binding flags for the search</param>
        /// <param name="parameterTypes">Optional parameter types to match signature</param>
        /// <returns>The MethodInfo if found, null otherwise</returns>
        public static MethodInfo GetMethod(Type type, string methodName, BindingFlags flags, Type[] parameterTypes = null)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var cacheKey = $"{type.FullName}.{methodName}.{(int)flags}";
            if (parameterTypes != null)
            {
                cacheKey += "." + string.Join(",", parameterTypes.Select(t => t.FullName));
            }

            if (_methodCache.TryGetValue(cacheKey, out var cached))
                return cached;

            MethodInfo method;
            if (parameterTypes != null)
            {
                method = type.GetMethod(methodName, flags, null, parameterTypes, null);
            }
            else
            {
                method = type.GetMethod(methodName, flags);
            }

            if (method != null)
            {
                _methodCache[cacheKey] = method;
            }

            return method;
        }

        /// <summary>
        /// Gets a property from a type using reflection with caching.
        /// </summary>
        public static PropertyInfo GetProperty(Type type, string propertyName, BindingFlags flags)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var cacheKey = $"{type.FullName}.{propertyName}.{(int)flags}";

            if (_propertyCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var property = type.GetProperty(propertyName, flags);
            if (property != null)
            {
                _propertyCache[cacheKey] = property;
            }

            return property;
        }

        /// <summary>
        /// Gets a field from a type using reflection with caching.
        /// </summary>
        public static FieldInfo GetField(Type type, string fieldName, BindingFlags flags)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var cacheKey = $"{type.FullName}.{fieldName}.{(int)flags}";

            if (_fieldCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var field = type.GetField(fieldName, flags);
            if (field != null)
            {
                _fieldCache[cacheKey] = field;
            }

            return field;
        }

        /// <summary>
        /// Invokes a method synchronously and returns the result.
        /// </summary>
        /// <param name="instance">The object instance (null for static methods)</param>
        /// <param name="method">The method to invoke</param>
        /// <param name="parameters">Method parameters</param>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The method's return value</returns>
        public static object InvokeMethod(object instance, MethodInfo method, object[] parameters, DiagnosticsLogger logger = null)
        {
            if (method == null)
            {
                throw new InvalidOperationException($"Method not found on type {instance?.GetType().FullName}");
            }

            try
            {
                return method.Invoke(instance, parameters);
            }
            catch (TargetInvocationException ex)
            {
                logger?.Error($"Method invocation failed: {ex.InnerException?.Message ?? ex.Message}");
                throw ex.InnerException ?? ex;
            }
        }

        /// <summary>
        /// Invokes an async method and awaits the result, handling IResponse&lt;T&gt; pattern.
        /// </summary>
        /// <typeparam name="T">Expected return type</typeparam>
        /// <param name="instance">The object instance (null for static methods)</param>
        /// <param name="method">The async method to invoke</param>
        /// <param name="parameters">Method parameters</param>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The awaited result, unwrapped from IResponse if applicable</returns>
        public static T InvokeMethodAsync<T>(object instance, MethodInfo method, object[] parameters, DiagnosticsLogger logger = null)
        {
            if (method == null)
            {
                throw new InvalidOperationException($"Method not found on type {instance?.GetType().FullName}");
            }

            try
            {
                var task = method.Invoke(instance, parameters);
                if (task == null)
                {
                    throw new InvalidOperationException($"Method {method.Name} returned null");
                }

                var taskResult = ((dynamic)task).GetAwaiter().GetResult();
                return HandleResponse<T>(taskResult, logger);
            }
            catch (TargetInvocationException ex)
            {
                logger?.Error($"Async method invocation failed: {ex.InnerException?.Message ?? ex.Message}");
                throw ex.InnerException ?? ex;
            }
        }

        /// <summary>
        /// Finds a type across assemblies, with static caching for deterministic lookups.
        /// Searches in order: static cache, parameter cache, searchFromType assembly, all DataExchange assemblies.
        /// </summary>
        /// <param name="typeName">Full type name to find</param>
        /// <param name="searchFromType">Optional type whose assembly to search first</param>
        /// <param name="cache">Optional local cache to populate</param>
        /// <returns>The Type if found, null otherwise</returns>
        public static Type FindType(string typeName, Type searchFromType = null, Dictionary<string, Type> cache = null)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // Check static cache first
            if (_typeCache.TryGetValue(typeName, out var cachedType))
            {
                cache?.TryAdd(typeName, cachedType);
                return cachedType;
            }

            // Check parameter cache
            if (cache != null && cache.TryGetValue(typeName, out cachedType))
            {
                _typeCache[typeName] = cachedType;
                return cachedType;
            }

            // Try searchFromType's assembly first (most common case)
            if (searchFromType != null)
            {
                var type = searchFromType.Assembly.GetType(typeName);
                if (type != null)
                {
                    _typeCache[typeName] = type;
                    cache?.TryAdd(typeName, type);
                    return type;
                }
            }

            // Search all DataExchange assemblies
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.Contains("DataExchange"))
                .ToList();

            foreach (var asm in allAssemblies)
            {
                var foundType = asm.GetType(typeName);
                if (foundType != null)
                {
                    _typeCache[typeName] = foundType;
                    cache?.TryAdd(typeName, foundType);
                    return foundType;
                }
            }

            return null;
        }

        /// <summary>
        /// Creates an instance and sets its ID using the SetId method (with caching).
        /// </summary>
        /// <param name="type">The type to instantiate</param>
        /// <param name="id">The ID to set</param>
        /// <param name="foundTypes">Optional type cache</param>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The created instance with ID set</returns>
        public static object CreateInstanceWithId(Type type, string id, Dictionary<string, Type> foundTypes = null, DiagnosticsLogger logger = null)
        {
            var instance = Activator.CreateInstance(type);
            if (instance == null)
            {
                throw new InvalidOperationException($"Failed to create instance of {type.FullName}");
            }

            // Cache SetId method lookup by type name
            var cacheKey = $"{type.FullName}.SetId";
            if (!_methodCache.TryGetValue(cacheKey, out var setIdMethod))
            {
                // Find SetId method (might be on base type)
                setIdMethod = type.GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
                if (setIdMethod == null)
                {
                    var baseType = type.BaseType;
                    if (baseType != null)
                    {
                        setIdMethod = baseType.GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                if (setIdMethod == null)
                {
                    throw new InvalidOperationException($"Could not find SetId method on {type.FullName} or its base types");
                }

                _methodCache[cacheKey] = setIdMethod;
            }

            setIdMethod.Invoke(instance, new object[] { id });
            return instance;
        }

        /// <summary>
        /// Handles IResponse&lt;T&gt; pattern, extracting Value property if present.
        /// Supports various SDK response patterns including IsSuccess/Success and Value properties.
        /// </summary>
        /// <typeparam name="T">Expected return type</typeparam>
        /// <param name="response">The response object</param>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The extracted value of type T</returns>
        public static T HandleResponse<T>(object response, DiagnosticsLogger logger = null)
        {
            if (response == null)
            {
                return default(T);
            }

            // Check if it's already the right type
            if (response is T directResult)
            {
                return directResult;
            }

            // Check for IResponse<T> pattern
            var responseType = response.GetType();
            var isSuccessProp = responseType.GetProperty("IsSuccess") ?? responseType.GetProperty("Success");

            if (isSuccessProp != null)
            {
                var isSuccess = (bool)isSuccessProp.GetValue(response);

                if (!isSuccess)
                {
                    var errorProp = responseType.GetProperty("Error");
                    if (errorProp != null)
                    {
                        var error = errorProp.GetValue(response);
                        throw new InvalidOperationException($"Operation failed: {error}");
                    }
                }
            }

            // Try to get Value property
            var valueProp = responseType.GetProperty("Value");
            if (valueProp != null)
            {
                var value = valueProp.GetValue(response);

                if (value != null)
                {
                    // Check if value type is assignable to T
                    var valueType = value.GetType();
                    var targetType = typeof(T);

                    if (targetType.IsAssignableFrom(valueType))
                    {
                        try
                        {
                            return (T)value;
                        }
                        catch (InvalidCastException)
                        {
                            // Fall through to pattern match
                        }
                    }

                    // Try 'is' pattern match as fallback
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                }
            }

            // Last resort: try direct cast on response itself
            try
            {
                return (T)response;
            }
            catch (Exception castEx)
            {
                var valueInfo = valueProp != null
                    ? $"Value type: {valueProp.GetValue(response)?.GetType().FullName ?? "null"}"
                    : "No Value property";
                throw new InvalidOperationException(
                    $"Could not convert response of type {responseType.FullName} to {typeof(T).FullName}. " +
                    $"{valueInfo}. Error: {castEx.Message}");
            }
        }

        /// <summary>
        /// Clears all caches. Use sparingly, mainly for testing or when assemblies change.
        /// </summary>
        public static void ClearCaches()
        {
            _typeCache.Clear();
            _methodCache.Clear();
            _propertyCache.Clear();
            _fieldCache.Clear();
            _constructorCache.Clear();
        }
    }
}
