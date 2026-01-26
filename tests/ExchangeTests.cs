using DataExchangeNodes.DataExchange;
using FluentAssertions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class ExchangeTests
    {
        #region FromJson - Null/Empty Input Tests

        [Test]
        public void FromJson_NullJson_ReturnsNull()
        {
            // Act
            var result = Exchange.FromJson(null);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void FromJson_EmptyJson_ReturnsNull()
        {
            // Act
            var result = Exchange.FromJson("");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void FromJson_WhitespaceJson_ThrowsArgumentException()
        {
            // Whitespace is not empty - it's invalid JSON that throws
            // Act
            Action act = () => Exchange.FromJson("   ");

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("Failed to parse Exchange JSON:*");
        }

        #endregion

        #region FromJson - Valid JSON Tests

        [Test]
        public void FromJson_ValidJsonWithAllFields_ParsesAllProperties()
        {
            // Arrange - using raw JSON to ensure exact key names match what Exchange.FromJson expects
            // Note: Date values are stored as strings, but JObject may parse ISO dates differently
            var json = @"{
                ""exchangeId"": ""exch-123"",
                ""collectionId"": ""coll-456"",
                ""exchangeTitle"": ""Test Exchange"",
                ""exchangeDescription"": ""A test exchange description"",
                ""projectName"": ""Test Project"",
                ""folderPath"": ""/root/folder"",
                ""createdBy"": ""user1@test.com"",
                ""updatedBy"": ""user2@test.com"",
                ""projectUrn"": ""urn:adsk:project:123"",
                ""fileUrn"": ""urn:adsk:file:456"",
                ""folderUrn"": ""urn:adsk:folder:789"",
                ""fileVersionId"": ""v1.0"",
                ""hubId"": ""hub-abc"",
                ""hubRegion"": ""US"",
                ""createTime"": ""2025-01-01"",
                ""updated"": ""2025-01-15"",
                ""timestamp"": ""2025-01-20"",
                ""schemaNamespace"": ""autodesk.exchange"",
                ""exchangeThumbnail"": ""/path/to/thumb.png"",
                ""projectType"": ""ACC"",
                ""isUpdateAvailable"": true
            }";

            // Act
            var result = Exchange.FromJson(json);

            // Assert - Core identifiers
            result.Should().NotBeNull();
            result.ExchangeId.Should().Be("exch-123");
            result.CollectionId.Should().Be("coll-456");

            // Human-readable info
            result.ExchangeTitle.Should().Be("Test Exchange");
            result.ExchangeDescription.Should().Be("A test exchange description");
            result.ProjectName.Should().Be("Test Project");
            result.FolderPath.Should().Be("/root/folder");

            // User info
            result.CreatedBy.Should().Be("user1@test.com");
            result.UpdatedBy.Should().Be("user2@test.com");

            // URNs and IDs
            result.ProjectUrn.Should().Be("urn:adsk:project:123");
            result.FileUrn.Should().Be("urn:adsk:file:456");
            result.FolderUrn.Should().Be("urn:adsk:folder:789");
            result.FileVersionId.Should().Be("v1.0");
            result.HubId.Should().Be("hub-abc");
            result.HubRegion.Should().Be("US");

            // Timestamps - just verify they are populated (format may vary)
            result.CreateTime.Should().NotBeNullOrEmpty();
            result.CreateTime.Should().Contain("2025");
            result.Updated.Should().NotBeNullOrEmpty();
            result.Updated.Should().Contain("2025");
            result.Timestamp.Should().NotBeNullOrEmpty();
            result.Timestamp.Should().Contain("2025");

            // Additional metadata
            result.SchemaNamespace.Should().Be("autodesk.exchange");
            result.ExchangeThumbnail.Should().Be("/path/to/thumb.png");
            result.ProjectType.Should().Be("ACC");
            result.IsUpdateAvailable.Should().BeTrue();
        }

        [Test]
        public void FromJson_MinimalJson_ParsesWithDefaultsForMissingFields()
        {
            // Arrange
            var json = JsonConvert.SerializeObject(new
            {
                exchangeId = "exch-123",
                collectionId = "coll-456"
            });

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.Should().NotBeNull();
            result.ExchangeId.Should().Be("exch-123");
            result.CollectionId.Should().Be("coll-456");
            result.ExchangeTitle.Should().Be("");
            result.ProjectName.Should().Be("");
            result.HubId.Should().Be("");
            result.IsUpdateAvailable.Should().BeFalse();
        }

        [Test]
        public void FromJson_EmptyObject_ReturnsExchangeWithEmptyStrings()
        {
            // Arrange
            var json = "{}";

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.Should().NotBeNull();
            result.ExchangeId.Should().Be("");
            result.CollectionId.Should().Be("");
            result.ExchangeTitle.Should().Be("");
        }

        #endregion

        #region FromJson - Boolean Parsing Tests

        [Test]
        public void FromJson_IsUpdateAvailableTrue_ParsesAsTrue()
        {
            // Arrange
            var json = JsonConvert.SerializeObject(new
            {
                exchangeId = "exch-123",
                isUpdateAvailable = true
            });

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.IsUpdateAvailable.Should().BeTrue();
        }

        [Test]
        public void FromJson_IsUpdateAvailableFalse_ParsesAsFalse()
        {
            // Arrange
            var json = JsonConvert.SerializeObject(new
            {
                exchangeId = "exch-123",
                isUpdateAvailable = false
            });

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.IsUpdateAvailable.Should().BeFalse();
        }

        [Test]
        public void FromJson_IsUpdateAvailableAsString_ParsesCorrectly()
        {
            // Arrange - Some APIs return booleans as strings
            var json = "{\"exchangeId\":\"exch-123\",\"isUpdateAvailable\":\"true\"}";

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.IsUpdateAvailable.Should().BeTrue();
        }

        [Test]
        public void FromJson_IsUpdateAvailableInvalidString_DefaultsToFalse()
        {
            // Arrange
            var json = "{\"exchangeId\":\"exch-123\",\"isUpdateAvailable\":\"invalid\"}";

            // Act
            var result = Exchange.FromJson(json);

            // Assert
            result.IsUpdateAvailable.Should().BeFalse();
        }

        #endregion

        #region FromJson - Error Handling Tests

        [Test]
        public void FromJson_MalformedJson_ThrowsArgumentException()
        {
            // Arrange
            string malformedJson = "{ not valid json";

            // Act
            Action act = () => Exchange.FromJson(malformedJson);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("Failed to parse Exchange JSON:*");
        }

        [Test]
        public void FromJson_InvalidJsonStructure_ThrowsArgumentException()
        {
            // Arrange
            string invalidJson = "[1, 2, 3]"; // Array instead of object

            // Act
            Action act = () => Exchange.FromJson(invalidJson);

            // Assert
            act.Should().Throw<ArgumentException>();
        }

        #endregion

        #region ToString Tests

        [Test]
        public void ToString_ReturnsFormattedString()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                ExchangeTitle = "My Exchange",
                ProjectName = "My Project"
            };

            // Act
            var result = exchange.ToString();

            // Assert
            result.Should().Be("Exchange: My Exchange (ID: exch-123, Project: My Project)");
        }

        [Test]
        public void ToString_WithEmptyValues_ReturnsFormattedStringWithEmptyPlaceholders()
        {
            // Arrange
            var exchange = new Exchange();

            // Act
            var result = exchange.ToString();

            // Assert
            result.Should().Be("Exchange:  (ID: , Project: )");
        }

        [Test]
        public void ToString_WithNullValues_HandlesGracefully()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = null,
                ExchangeTitle = null,
                ProjectName = null
            };

            // Act
            var result = exchange.ToString();

            // Assert
            result.Should().Contain("Exchange:");
        }

        #endregion

        #region Property Tests

        [Test]
        public void Exchange_DefaultConstructor_InitializesWithNullProperties()
        {
            // Act
            var exchange = new Exchange();

            // Assert
            exchange.ExchangeId.Should().BeNull();
            exchange.CollectionId.Should().BeNull();
            exchange.ExchangeTitle.Should().BeNull();
            exchange.IsUpdateAvailable.Should().BeFalse();
        }

        [Test]
        public void Exchange_Properties_CanBeSetAndRetrieved()
        {
            // Arrange
            var exchange = new Exchange();

            // Act
            exchange.ExchangeId = "test-id";
            exchange.CollectionId = "test-collection";
            exchange.ExchangeTitle = "Test Title";
            exchange.HubId = "hub-id";
            exchange.IsUpdateAvailable = true;

            // Assert
            exchange.ExchangeId.Should().Be("test-id");
            exchange.CollectionId.Should().Be("test-collection");
            exchange.ExchangeTitle.Should().Be("Test Title");
            exchange.HubId.Should().Be("hub-id");
            exchange.IsUpdateAvailable.Should().BeTrue();
        }

        #endregion
    }
}
