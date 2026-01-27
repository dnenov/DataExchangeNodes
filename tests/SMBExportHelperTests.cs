using System;
using System.IO;
using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class SMBExportHelperTests
    {
        private DiagnosticsLogger _logger;
        private string _tempFilePath;

        [SetUp]
        public void SetUp()
        {
            _logger = new DiagnosticsLogger(DiagnosticLevel.Debug);
            // Create a temp file for file existence tests
            _tempFilePath = Path.GetTempFileName();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up temp file
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }

        #region ValidateInputs Tests

        [Test]
        public void ValidateInputs_WithNullExchange_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => SMBExportHelper.ValidateInputs(null, _tempFilePath, _logger);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("exchange")
                .WithMessage("*Exchange cannot be null*");
        }

        [Test]
        public void ValidateInputs_WithNullExchangeId_ThrowsArgumentException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = null,
                CollectionId = "coll-123"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("exchange")
                .WithMessage("*ExchangeId is required*");
        }

        [Test]
        public void ValidateInputs_WithEmptyExchangeId_ThrowsArgumentException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "",
                CollectionId = "coll-123"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("exchange")
                .WithMessage("*ExchangeId is required*");
        }

        [Test]
        public void ValidateInputs_WithNullCollectionId_ThrowsArgumentException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = null
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("exchange")
                .WithMessage("*CollectionId is required*");
        }

        [Test]
        public void ValidateInputs_WithEmptyCollectionId_ThrowsArgumentException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = ""
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("exchange")
                .WithMessage("*CollectionId is required*");
        }

        [Test]
        public void ValidateInputs_WithNullFilePath_ThrowsFileNotFoundException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, null, _logger);

            // Assert
            act.Should().Throw<FileNotFoundException>()
                .WithMessage("*SMB file not found*");
        }

        [Test]
        public void ValidateInputs_WithEmptyFilePath_ThrowsFileNotFoundException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, "", _logger);

            // Assert
            act.Should().Throw<FileNotFoundException>()
                .WithMessage("*SMB file not found*");
        }

        [Test]
        public void ValidateInputs_WithNonExistentFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456"
            };
            var nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_file_" + Guid.NewGuid() + ".smb");

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, nonExistentPath, _logger);

            // Assert
            act.Should().Throw<FileNotFoundException>()
                .WithMessage($"*SMB file not found: {nonExistentPath}*");
        }

        [Test]
        public void ValidateInputs_WithValidInputs_DoesNotThrow()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().NotThrow();
        }

        [Test]
        public void ValidateInputs_WithValidInputsAndHubId_DoesNotThrow()
        {
            // Arrange
            var exchange = new Exchange
            {
                ExchangeId = "exch-123",
                CollectionId = "coll-456",
                HubId = "hub-789"
            };

            // Act
            Action act = () => SMBExportHelper.ValidateInputs(exchange, _tempFilePath, _logger);

            // Assert
            act.Should().NotThrow();
        }

        #endregion
    }
}
