using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class DiagnosticsLoggerTests
    {
        #region Constructor Tests

        [Test]
        public void Constructor_DefaultLevel_IsError()
        {
            // Act
            var logger = new DiagnosticsLogger();

            // Assert - only error messages should be logged by default
            logger.Info("This should not be logged");
            logger.Error("This should be logged");

            logger.Count.Should().Be(1);
        }

        [Test]
        public void Constructor_WithInfoLevel_LogsInfoAndBelow()
        {
            // Act
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);

            logger.Error("Error message");
            logger.Warning("Warning message");
            logger.Info("Info message");
            logger.Debug("Debug message"); // Should not be logged

            // Assert
            logger.Count.Should().Be(3);
        }

        [Test]
        public void Constructor_WithDebugLevel_LogsEverything()
        {
            // Act
            var logger = new DiagnosticsLogger(DiagnosticLevel.Debug);

            logger.Error("Error message");
            logger.Warning("Warning message");
            logger.Info("Info message");
            logger.Debug("Debug message");

            // Assert
            logger.Count.Should().Be(4);
        }

        #endregion

        #region Logging Methods Tests

        [Test]
        public void Error_AddsErrorPrefix()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Error);

            // Act
            logger.Error("Something went wrong");

            // Assert
            logger.GetLog().Should().Contain("ERROR: Something went wrong");
        }

        [Test]
        public void Warning_AddsWarningPrefix()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Warning);

            // Act
            logger.Warning("Something might be wrong");

            // Assert
            logger.GetLog().Should().Contain("WARNING: Something might be wrong");
        }

        [Test]
        public void Info_AddsMessageWithoutPrefix()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);

            // Act
            logger.Info("Information message");

            // Assert
            logger.GetLog().Should().Be("Information message");
        }

        [Test]
        public void Debug_AddsMessageWithoutPrefix()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Debug);

            // Act
            logger.Debug("Debug info");

            // Assert
            logger.GetLog().Should().Be("Debug info");
        }

        [Test]
        public void Success_LogsAtInfoLevel()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);

            // Act
            logger.Success("Operation completed");

            // Assert
            logger.GetLog().Should().Be("Operation completed");
        }

        [Test]
        public void Add_LogsAtInfoLevel()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);

            // Act
            logger.Add("Added message");

            // Assert
            logger.GetLog().Should().Be("Added message");
        }

        #endregion

        #region GetMessages Tests

        [Test]
        public void GetMessages_ReturnsListOfMessages()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Message 1");
            logger.Info("Message 2");

            // Act
            var messages = logger.GetMessages();

            // Assert
            messages.Should().HaveCount(2);
            messages[0].Should().Be("Message 1");
            messages[1].Should().Be("Message 2");
        }

        [Test]
        public void GetMessages_ReturnsNewListInstance()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Message");

            // Act
            var messages1 = logger.GetMessages();
            var messages2 = logger.GetMessages();

            // Assert - should be different list instances
            messages1.Should().NotBeSameAs(messages2);
        }

        [Test]
        public void GetMessages_ModifyingReturnedList_DoesNotAffectLogger()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Original");

            // Act
            var messages = logger.GetMessages();
            messages.Add("Modified");

            // Assert
            logger.Count.Should().Be(1);
        }

        #endregion

        #region GetLog Tests

        [Test]
        public void GetLog_JoinsMessagesWithNewline()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Info);
            logger.Info("Line 1");
            logger.Info("Line 2");
            logger.Info("Line 3");

            // Act
            var log = logger.GetLog();

            // Assert
            log.Should().Be("Line 1\nLine 2\nLine 3");
        }

        [Test]
        public void GetLog_EmptyLogger_ReturnsEmptyString()
        {
            // Arrange
            var logger = new DiagnosticsLogger();

            // Act
            var log = logger.GetLog();

            // Assert
            log.Should().BeEmpty();
        }

        #endregion

        #region Count Tests

        [Test]
        public void Count_EmptyLogger_ReturnsZero()
        {
            // Arrange
            var logger = new DiagnosticsLogger();

            // Assert
            logger.Count.Should().Be(0);
        }

        [Test]
        public void Count_AfterLogging_ReturnsCorrectCount()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Debug);

            // Act
            logger.Error("1");
            logger.Warning("2");
            logger.Info("3");

            // Assert
            logger.Count.Should().Be(3);
        }

        #endregion

        #region Level Filtering Tests

        [Test]
        public void ErrorLevel_OnlyLogsErrors()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Error);

            // Act
            logger.Error("Error");
            logger.Warning("Warning");
            logger.Info("Info");
            logger.Debug("Debug");

            // Assert
            logger.Count.Should().Be(1);
            logger.GetLog().Should().Contain("ERROR: Error");
        }

        [Test]
        public void WarningLevel_LogsErrorsAndWarnings()
        {
            // Arrange
            var logger = new DiagnosticsLogger(DiagnosticLevel.Warning);

            // Act
            logger.Error("Error");
            logger.Warning("Warning");
            logger.Info("Info");
            logger.Debug("Debug");

            // Assert
            logger.Count.Should().Be(2);
        }

        #endregion
    }
}
