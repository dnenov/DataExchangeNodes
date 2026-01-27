using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    /// <summary>
    /// Tests for DataExchangeClient.
    /// Note: Most tests require the Autodesk.DataExchange SDK assembly which is only
    /// available at runtime within Dynamo. These tests are marked as integration tests.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class DataExchangeClientTests
    {
        #region IsInitialized Tests
        // Note: These tests require SDK assembly loading which fails in unit test environment

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void IsInitialized_BeforeInitialization_ReturnsFalse()
        {
            // Act
            var result = DataExchangeClient.IsInitialized();

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void IsInitialized_AfterReset_ReturnsFalse()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act
            var result = DataExchangeClient.IsInitialized();

            // Assert
            result.Should().BeFalse();
        }

        #endregion

        #region GetClient Tests

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void GetClient_BeforeInitialization_ReturnsNull()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act
            var result = DataExchangeClient.GetClient();

            // Assert
            result.Should().BeNull();
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void GetClient_AfterReset_ReturnsNull()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act
            var result = DataExchangeClient.GetClient();

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region GetSDKOptions Tests

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void GetSDKOptions_BeforeInitialization_ReturnsNull()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act
            var result = DataExchangeClient.GetSDKOptions();

            // Assert
            result.Should().BeNull();
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void GetSDKOptions_AfterReset_ReturnsNull()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act
            var result = DataExchangeClient.GetSDKOptions();

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Reset Tests

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void Reset_WhenCalledMultipleTimes_DoesNotThrow()
        {
            // Act & Assert - should not throw
            DataExchangeClient.Reset();
            DataExchangeClient.Reset();
            DataExchangeClient.Reset();

            DataExchangeClient.IsInitialized().Should().BeFalse();
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void Reset_ClearsAllState()
        {
            // Arrange
            DataExchangeClient.Reset();

            // Act & Assert
            DataExchangeClient.IsInitialized().Should().BeFalse();
            DataExchangeClient.GetClient().Should().BeNull();
            DataExchangeClient.GetSDKOptions().Should().BeNull();
        }

        #endregion

        #region InitializeClient Tests

        [Test]
        [Ignore("Requires Autodesk.DataExchange SDK - integration test only")]
        public void InitializeClient_WithNullOptions_ThrowsArgumentNullException()
        {
            // Act
            var act = () => DataExchangeClient.InitializeClient(null);

            // Assert
            act.Should().Throw<System.ArgumentNullException>()
                .WithParameterName("sdkOptions");
        }

        #endregion
    }
}
