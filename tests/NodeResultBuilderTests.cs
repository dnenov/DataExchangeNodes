using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class NodeResultBuilderTests
    {
        #region Fluent Builder Tests

        [Test]
        public void WithSuccess_True_SetSuccessToTrue()
        {
            // Act
            var result = new NodeResultBuilder()
                .WithSuccess(true)
                .Build();

            // Assert
            result["success"].Should().Be(true);
        }

        [Test]
        public void WithSuccess_False_SetSuccessToFalse()
        {
            // Act
            var result = new NodeResultBuilder()
                .WithSuccess(false)
                .Build();

            // Assert
            result["success"].Should().Be(false);
        }

        [Test]
        public void WithDiagnostics_FromLogger_AddsDiagnosticsString()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Test message");

            // Act
            var result = new NodeResultBuilder()
                .WithDiagnostics(logger)
                .Build();

            // Assert
            result["diagnostics"].Should().Be("Test message");
        }

        [Test]
        public void WithDiagnostics_FromList_JoinsWithNewline()
        {
            // Arrange
            var diagnostics = new List<string> { "Line 1", "Line 2" };

            // Act
            var result = new NodeResultBuilder()
                .WithDiagnostics(diagnostics)
                .Build();

            // Assert
            result["diagnostics"].Should().Be("Line 1\nLine 2");
        }

        [Test]
        public void WithDiagnostics_FromString_AddsDiagnosticsDirectly()
        {
            // Act
            var result = new NodeResultBuilder()
                .WithDiagnostics("Simple message")
                .Build();

            // Assert
            result["diagnostics"].Should().Be("Simple message");
        }

        [Test]
        public void WithProperty_AddsCustomProperty()
        {
            // Act
            var result = new NodeResultBuilder()
                .WithProperty("customKey", "customValue")
                .Build();

            // Assert
            result["customKey"].Should().Be("customValue");
        }

        [Test]
        public void WithProperty_CanAddMultipleProperties()
        {
            // Act
            var result = new NodeResultBuilder()
                .WithProperty("key1", "value1")
                .WithProperty("key2", 42)
                .WithProperty("key3", true)
                .Build();

            // Assert
            result["key1"].Should().Be("value1");
            result["key2"].Should().Be(42);
            result["key3"].Should().Be(true);
        }

        [Test]
        public void WithLog_FromLogger_AddsLogKey()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Log entry");

            // Act
            var result = new NodeResultBuilder()
                .WithLog(logger)
                .Build();

            // Assert
            result["log"].Should().Be("Log entry");
        }

        [Test]
        public void WithLog_FromList_JoinsWithNewline()
        {
            // Arrange
            var log = new List<string> { "Entry 1", "Entry 2" };

            // Act
            var result = new NodeResultBuilder()
                .WithLog(log)
                .Build();

            // Assert
            result["log"].Should().Be("Entry 1\nEntry 2");
        }

        #endregion

        #region Build Tests

        [Test]
        public void Build_ReturnsNewDictionaryInstance()
        {
            // Arrange
            var builder = new NodeResultBuilder()
                .WithSuccess(true);

            // Act
            var result1 = builder.Build();
            var result2 = builder.Build();

            // Assert
            result1.Should().NotBeSameAs(result2);
        }

        [Test]
        public void Build_WithLoggerSet_AutoAddsDiagnostics()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Auto-added");

            // Act
            var result = new NodeResultBuilder()
                .WithDiagnostics(logger)
                .Build();

            // Assert
            result.Should().ContainKey("diagnostics");
            result["diagnostics"].Should().Be("Auto-added");
        }

        [Test]
        public void Build_DiagnosticsAlreadySet_DoesNotOverwrite()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Logger message");

            // Act - When diagnostics is explicitly set first, logger won't overwrite it in Build()
            var result = new NodeResultBuilder()
                .WithDiagnostics("Explicit message")
                .WithDiagnostics(logger) // This sets _logger but diagnostics key already exists
                .Build();

            // Assert - The explicit message takes precedence since it was set directly on the dictionary
            result["diagnostics"].Should().Be("Explicit message");
        }

        #endregion

        #region Static Error Method Tests

        [Test]
        public void Error_WithLogger_ReturnsErrorResult()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Error);
            logger.Error("Something failed");

            // Act
            var result = NodeResultBuilder.Error(logger);

            // Assert
            result["success"].Should().Be(false);
            result["diagnostics"].Should().Be("ERROR: Something failed");
        }

        [Test]
        public void Error_WithAdditionalProperties_IncludesProperties()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Error);
            logger.Error("Failed");

            // Act
            var result = NodeResultBuilder.Error(logger,
                ("errorCode", 500),
                ("details", "More info"));

            // Assert
            result["success"].Should().Be(false);
            result["errorCode"].Should().Be(500);
            result["details"].Should().Be("More info");
        }

        [Test]
        public void Error_WithList_ReturnsErrorResult()
        {
            // Arrange
            var diagnostics = new List<string> { "Error 1", "Error 2" };

            // Act
            var result = NodeResultBuilder.Error(diagnostics);

            // Assert
            result["success"].Should().Be(false);
            result["diagnostics"].Should().Be("Error 1\nError 2");
        }

        [Test]
        public void Error_WithListAndProperties_IncludesAll()
        {
            // Arrange
            var diagnostics = new List<string> { "Error message" };

            // Act
            var result = NodeResultBuilder.Error(diagnostics, ("count", 1));

            // Assert
            result["success"].Should().Be(false);
            result["diagnostics"].Should().Be("Error message");
            result["count"].Should().Be(1);
        }

        #endregion

        #region Static Ok Method Tests

        [Test]
        public void Ok_WithLogger_ReturnsSuccessResult()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Operation completed");

            // Act
            var result = NodeResultBuilder.Ok(logger);

            // Assert
            result["success"].Should().Be(true);
            result["diagnostics"].Should().Be("Operation completed");
        }

        [Test]
        public void Ok_WithAdditionalProperties_IncludesProperties()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Done");

            // Act
            var result = NodeResultBuilder.Ok(logger,
                ("itemsProcessed", 10),
                ("outputPath", "/path/to/output"));

            // Assert
            result["success"].Should().Be(true);
            result["itemsProcessed"].Should().Be(10);
            result["outputPath"].Should().Be("/path/to/output");
        }

        #endregion

        #region Chaining Tests

        [Test]
        public void FluentChaining_BuildsCompleteResult()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Processing complete");

            // Act
            var result = new NodeResultBuilder()
                .WithSuccess(true)
                .WithDiagnostics(logger)
                .WithProperty("count", 5)
                .WithProperty("elapsed", 1.5)
                .Build();

            // Assert
            result.Should().HaveCount(4);
            result["success"].Should().Be(true);
            result["diagnostics"].Should().Be("Processing complete");
            result["count"].Should().Be(5);
            result["elapsed"].Should().Be(1.5);
        }

        #endregion
    }
}
