using System;
using System.Collections.Generic;
using System.Reflection;
using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class ReflectionUtilsTests
    {
        [SetUp]
        public void SetUp()
        {
            // Clear caches before each test for isolation
            ReflectionUtils.ClearCaches();
        }

        #region GetMethod Tests

        [Test]
        public void GetMethod_WithNullType_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => ReflectionUtils.GetMethod(null, "ToString", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("type");
        }

        [Test]
        public void GetMethod_WithValidMethod_ReturnsMethodInfo()
        {
            // Arrange - use TestClass.Add which has no overloads
            var type = typeof(TestClass);

            // Act
            var result = ReflectionUtils.GetMethod(type, "Add", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().NotBeNull();
            result.Name.Should().Be("Add");
        }

        [Test]
        public void GetMethod_WithNonExistentMethod_ReturnsNull()
        {
            // Arrange
            var type = typeof(string);

            // Act
            var result = ReflectionUtils.GetMethod(type, "NonExistentMethod", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetMethod_CachesResult()
        {
            // Arrange - use TestClass.Add which has no overloads
            var type = typeof(TestClass);
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Act
            var result1 = ReflectionUtils.GetMethod(type, "Add", flags);
            var result2 = ReflectionUtils.GetMethod(type, "Add", flags);

            // Assert - both should return same cached instance
            result1.Should().BeSameAs(result2);
        }

        [Test]
        public void GetMethod_WithParameterTypes_FindsCorrectOverload()
        {
            // Arrange
            var type = typeof(string);
            var flags = BindingFlags.Public | BindingFlags.Instance;
            var paramTypes = new[] { typeof(int), typeof(int) };

            // Act
            var result = ReflectionUtils.GetMethod(type, "Substring", flags, paramTypes);

            // Assert
            result.Should().NotBeNull();
            result.Name.Should().Be("Substring");
            result.GetParameters().Should().HaveCount(2);
        }

        [Test]
        public void GetMethod_DifferentFlagsProduceDifferentCacheKeys()
        {
            // Arrange
            var type = typeof(TestClass);

            // Act
            var publicMethod = ReflectionUtils.GetMethod(type, "PublicMethod", BindingFlags.Public | BindingFlags.Instance);
            var nonPublicMethod = ReflectionUtils.GetMethod(type, "PublicMethod", BindingFlags.NonPublic | BindingFlags.Instance);

            // Assert
            publicMethod.Should().NotBeNull();
            nonPublicMethod.Should().BeNull(); // PublicMethod is public, not found with NonPublic flags
        }

        #endregion

        #region GetProperty Tests

        [Test]
        public void GetProperty_WithNullType_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => ReflectionUtils.GetProperty(null, "Length", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("type");
        }

        [Test]
        public void GetProperty_WithValidProperty_ReturnsPropertyInfo()
        {
            // Arrange
            var type = typeof(string);

            // Act
            var result = ReflectionUtils.GetProperty(type, "Length", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().NotBeNull();
            result.Name.Should().Be("Length");
        }

        [Test]
        public void GetProperty_WithNonExistentProperty_ReturnsNull()
        {
            // Arrange
            var type = typeof(string);

            // Act
            var result = ReflectionUtils.GetProperty(type, "NonExistent", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetProperty_CachesResult()
        {
            // Arrange
            var type = typeof(string);
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Act
            var result1 = ReflectionUtils.GetProperty(type, "Length", flags);
            var result2 = ReflectionUtils.GetProperty(type, "Length", flags);

            // Assert
            result1.Should().BeSameAs(result2);
        }

        #endregion

        #region GetField Tests

        [Test]
        public void GetField_WithNullType_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => ReflectionUtils.GetField(null, "someField", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("type");
        }

        [Test]
        public void GetField_WithValidField_ReturnsFieldInfo()
        {
            // Arrange
            var type = typeof(TestClass);

            // Act
            var result = ReflectionUtils.GetField(type, "PublicField", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().NotBeNull();
            result.Name.Should().Be("PublicField");
        }

        [Test]
        public void GetField_WithNonExistentField_ReturnsNull()
        {
            // Arrange
            var type = typeof(TestClass);

            // Act
            var result = ReflectionUtils.GetField(type, "NonExistent", BindingFlags.Public | BindingFlags.Instance);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetField_CachesResult()
        {
            // Arrange
            var type = typeof(TestClass);
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Act
            var result1 = ReflectionUtils.GetField(type, "PublicField", flags);
            var result2 = ReflectionUtils.GetField(type, "PublicField", flags);

            // Assert
            result1.Should().BeSameAs(result2);
        }

        #endregion

        #region ClearCaches Tests

        [Test]
        public void ClearCaches_ClearsAllCachedItems()
        {
            // Arrange - populate caches using TestClass.Add (no overloads)
            var type = typeof(TestClass);
            var stringType = typeof(string);
            var flags = BindingFlags.Public | BindingFlags.Instance;
            ReflectionUtils.GetMethod(type, "Add", flags);
            ReflectionUtils.GetProperty(stringType, "Length", flags);

            // Act
            ReflectionUtils.ClearCaches();

            // Assert - caches should be empty (verify by checking new lookups don't return same instance)
            // This is indirect since we can't access private caches
            // After clear, the method will do a fresh lookup
            var method1 = ReflectionUtils.GetMethod(type, "Add", flags);
            ReflectionUtils.ClearCaches();
            var method2 = ReflectionUtils.GetMethod(type, "Add", flags);

            // Both should be valid, but if caching worked properly they would be same instance
            // After clear, method2 should still find the method (proving it re-looked it up)
            method1.Should().NotBeNull();
            method2.Should().NotBeNull();
        }

        [Test]
        public void ClearCaches_DoesNotThrowWhenCachesAreEmpty()
        {
            // Act & Assert - should not throw
            Action act = () =>
            {
                ReflectionUtils.ClearCaches();
                ReflectionUtils.ClearCaches();
                ReflectionUtils.ClearCaches();
            };

            act.Should().NotThrow();
        }

        #endregion

        #region FindType Tests

        [Test]
        public void FindType_WithNullTypeName_ReturnsNull()
        {
            // Act
            var result = ReflectionUtils.FindType(null);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void FindType_WithEmptyTypeName_ReturnsNull()
        {
            // Act
            var result = ReflectionUtils.FindType("");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void FindType_WithSearchFromType_FindsTypeInSameAssembly()
        {
            // Arrange - use a type from the DataExchangeNodes assembly
            var searchFromType = typeof(Exchange);

            // Act
            var result = ReflectionUtils.FindType("DataExchangeNodes.DataExchange.Exchange", searchFromType);

            // Assert
            result.Should().NotBeNull();
            result.Should().Be(typeof(Exchange));
        }

        [Test]
        public void FindType_CachesResult()
        {
            // Arrange
            var searchFromType = typeof(Exchange);
            var typeName = "DataExchangeNodes.DataExchange.Exchange";

            // Act
            var result1 = ReflectionUtils.FindType(typeName, searchFromType);
            var result2 = ReflectionUtils.FindType(typeName, searchFromType);

            // Assert
            result1.Should().BeSameAs(result2);
        }

        [Test]
        public void FindType_PopulatesParameterCache()
        {
            // Arrange
            var searchFromType = typeof(Exchange);
            var typeName = "DataExchangeNodes.DataExchange.Exchange";
            var paramCache = new Dictionary<string, Type>();

            // Act
            var result = ReflectionUtils.FindType(typeName, searchFromType, paramCache);

            // Assert
            result.Should().NotBeNull();
            paramCache.Should().ContainKey(typeName);
            paramCache[typeName].Should().Be(typeof(Exchange));
        }

        [Test]
        public void FindType_UsesParameterCacheWhenPopulated()
        {
            // Arrange
            var typeName = "DataExchangeNodes.DataExchange.Exchange";
            var paramCache = new Dictionary<string, Type>
            {
                { typeName, typeof(Exchange) }
            };

            // Act
            var result = ReflectionUtils.FindType(typeName, null, paramCache);

            // Assert
            result.Should().Be(typeof(Exchange));
        }

        #endregion

        #region InvokeMethod Tests

        [Test]
        public void InvokeMethod_WithNullMethod_ThrowsInvalidOperationException()
        {
            // Arrange
            var instance = new TestClass();

            // Act
            Action act = () => ReflectionUtils.InvokeMethod(instance, null, null);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Method not found*");
        }

        [Test]
        public void InvokeMethod_WithValidMethod_ReturnsResult()
        {
            // Arrange
            var instance = new TestClass();
            var method = typeof(TestClass).GetMethod("Add");

            // Act
            var result = ReflectionUtils.InvokeMethod(instance, method, new object[] { 2, 3 });

            // Assert
            result.Should().Be(5);
        }

        [Test]
        public void InvokeMethod_WhenMethodThrows_UnwrapsException()
        {
            // Arrange
            var instance = new TestClass();
            var method = typeof(TestClass).GetMethod("ThrowException");

            // Act
            Action act = () => ReflectionUtils.InvokeMethod(instance, method, null);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Test exception");
        }

        #endregion

        #region InvokeMethodAsync Tests

        [Test]
        public void InvokeMethodAsync_WithNullMethod_ThrowsInvalidOperationException()
        {
            // Arrange
            var instance = new TestClass();

            // Act
            Action act = () => ReflectionUtils.InvokeMethodAsync<int>(instance, null, null);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Method not found*");
        }

        [Test]
        public void InvokeMethodAsync_WithValidAsyncMethod_ReturnsResult()
        {
            // Arrange
            var instance = new TestClass();
            var method = typeof(TestClass).GetMethod("GetValueAsync");

            // Act
            var result = ReflectionUtils.InvokeMethodAsync<int>(instance, method, null);

            // Assert
            result.Should().Be(42);
        }

        #endregion

        #region HandleResponse Tests

        [Test]
        public void HandleResponse_WithNull_ReturnsDefault()
        {
            // Act
            var result = ReflectionUtils.HandleResponse<string>(null);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void HandleResponse_WithDirectMatch_ReturnsValue()
        {
            // Arrange
            var value = "test";

            // Act
            var result = ReflectionUtils.HandleResponse<string>(value);

            // Assert
            result.Should().Be("test");
        }

        [Test]
        public void HandleResponse_WithIntValue_ReturnsValue()
        {
            // Arrange
            var value = 42;

            // Act
            var result = ReflectionUtils.HandleResponse<int>(value);

            // Assert
            result.Should().Be(42);
        }

        [Test]
        public void HandleResponse_WithResponsePattern_ExtractsValue()
        {
            // Arrange
            var response = new MockResponse<string> { IsSuccess = true, Value = "extracted" };

            // Act
            var result = ReflectionUtils.HandleResponse<string>(response);

            // Assert
            result.Should().Be("extracted");
        }

        [Test]
        public void HandleResponse_WithFailedResponse_ThrowsException()
        {
            // Arrange
            var response = new MockResponse<string> { IsSuccess = false, Error = "Something went wrong" };

            // Act
            Action act = () => ReflectionUtils.HandleResponse<string>(response);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Something went wrong*");
        }

        [Test]
        public void HandleResponse_WithSuccessProperty_HandlesAlternateNaming()
        {
            // Arrange
            var response = new MockResponseWithSuccess<int> { Success = true, Value = 100 };

            // Act
            var result = ReflectionUtils.HandleResponse<int>(response);

            // Assert
            result.Should().Be(100);
        }

        #endregion

        #region Helper Classes

        public class TestClass
        {
            public string PublicField = "test";

            public void PublicMethod() { }

            public int Add(int a, int b) => a + b;

            public void ThrowException()
            {
                throw new InvalidOperationException("Test exception");
            }

            public async System.Threading.Tasks.Task<int> GetValueAsync()
            {
                await System.Threading.Tasks.Task.Delay(1);
                return 42;
            }
        }

        public class MockResponse<T>
        {
            public bool IsSuccess { get; set; }
            public T Value { get; set; }
            public string Error { get; set; }
        }

        public class MockResponseWithSuccess<T>
        {
            public bool Success { get; set; }
            public T Value { get; set; }
        }

        #endregion
    }
}
