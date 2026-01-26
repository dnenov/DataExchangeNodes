using DataExchangeNodes.DataExchange;
using FluentAssertions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class DataExchangeUtilsTests
    {
        #region ConvertUnitToMmPerUnit Tests

        [Test]
        public void ConvertUnitToMmPerUnit_Meter_Returns1000()
        {
            // Arrange
            string unit = "kUnitType_Meter";

            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(1000.0);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_CentiMeter_Returns10()
        {
            // Arrange
            string unit = "kUnitType_CentiMeter";

            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(10.0);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_Feet_Returns304Point8()
        {
            // Arrange
            string unit = "kUnitType_Feet";

            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(304.8);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_Inch_Returns25Point4()
        {
            // Arrange
            string unit = "kUnitType_Inch";

            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(25.4);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_NullInput_ReturnsDefaultCm()
        {
            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(null);

            // Assert
            result.Should().Be(10.0);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_EmptyInput_ReturnsDefaultCm()
        {
            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit("");

            // Assert
            result.Should().Be(10.0);
        }

        [Test]
        public void ConvertUnitToMmPerUnit_InvalidUnit_ReturnsDefaultCm()
        {
            // Arrange
            string unit = "kUnitType_Unknown";

            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(10.0);
        }

        [Test]
        [TestCase("cm", 10.0)]
        [TestCase("ft", 304.8)]
        [TestCase("in", 25.4)]
        public void ConvertUnitToMmPerUnit_ShorthandUnits_ReturnsCorrectValue(string unit, double expected)
        {
            // Act
            double result = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region GetAvailableUnits Tests

        [Test]
        public void GetAvailableUnits_ReturnsExactlyFourUnits()
        {
            // Act
            var units = DataExchangeUtils.GetAvailableUnits();

            // Assert
            units.Should().HaveCount(4);
        }

        [Test]
        public void GetAvailableUnits_ContainsExpectedUnits()
        {
            // Act
            var units = DataExchangeUtils.GetAvailableUnits();

            // Assert
            units.Should().Contain("kUnitType_Meter");
            units.Should().Contain("kUnitType_CentiMeter");
            units.Should().Contain("kUnitType_Feet");
            units.Should().Contain("kUnitType_Inch");
        }

        [Test]
        public void GetAvailableUnits_ReturnsNewListInstance()
        {
            // Act
            var units1 = DataExchangeUtils.GetAvailableUnits();
            var units2 = DataExchangeUtils.GetAvailableUnits();

            // Assert - should be different instances
            units1.Should().NotBeSameAs(units2);
        }

        #endregion

        #region GetExchangeFromSelection Tests

        [Test]
        public void GetExchangeFromSelection_NullJson_ReturnsNull()
        {
            // Act
            var result = DataExchangeUtils.GetExchangeFromSelection(null);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetExchangeFromSelection_EmptyJson_ReturnsNull()
        {
            // Act
            var result = DataExchangeUtils.GetExchangeFromSelection("");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetExchangeFromSelection_ValidJson_ReturnsExchange()
        {
            // Arrange
            var json = JsonConvert.SerializeObject(new
            {
                exchangeId = "test-exchange-123",
                collectionId = "test-collection-456",
                exchangeTitle = "Test Exchange"
            });

            // Act
            var result = DataExchangeUtils.GetExchangeFromSelection(json);

            // Assert
            result.Should().NotBeNull();
            result.ExchangeId.Should().Be("test-exchange-123");
            result.CollectionId.Should().Be("test-collection-456");
            result.ExchangeTitle.Should().Be("Test Exchange");
        }

        [Test]
        public void GetExchangeFromSelection_MalformedJson_ThrowsArgumentException()
        {
            // Arrange
            string malformedJson = "{ invalid json }";

            // Act
            Action act = () => DataExchangeUtils.GetExchangeFromSelection(malformedJson);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("Failed to parse exchange data:*");
        }

        #endregion

        #region CreateIdentifier Tests
        // Note: CreateIdentifier tests are skipped because they require Autodesk.DataExchange.Core
        // assembly which is only available at runtime within Dynamo, not in the test environment.
        // These methods would need integration testing within the Dynamo context.

        [Test]
        [Ignore("Requires Autodesk.DataExchange.Core SDK - integration test only")]
        public void CreateIdentifier_NullExchange_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => DataExchangeUtils.CreateIdentifier(null);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("exchange");
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange.Core SDK - integration test only")]
        public void CreateIdentifier_ValidExchange_ReturnsIdentifierWithIds()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456"
            };

            // Act
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Assert
            identifier.Should().NotBeNull();
            identifier.ExchangeId.Should().Be("exch-123");
            identifier.CollectionId.Should().Be("coll-456");
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange.Core SDK - integration test only")]
        public void CreateIdentifier_ExchangeWithHubId_SetsHubIdOnIdentifier()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456",
                HubId = "hub-789"
            };

            // Act
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Assert
            identifier.HubId.Should().Be("hub-789");
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange.Core SDK - integration test only")]
        public void CreateIdentifier_ExchangeWithoutHubId_DoesNotSetHubId()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456",
                HubId = null
            };

            // Act
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Assert
            identifier.HubId.Should().BeNull();
        }

        [Test]
        [Ignore("Requires Autodesk.DataExchange.Core SDK - integration test only")]
        public void CreateIdentifier_ExchangeWithEmptyHubId_DoesNotSetHubId()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456",
                HubId = ""
            };

            // Act
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Assert
            identifier.HubId.Should().BeNull();
        }

        #endregion
    }
}
