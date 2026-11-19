using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class ByteUtilsTests
{
    [TestMethod]
    public void HexToBytes_StandardSpacedString_ParsesAccurately()
    {
        // Arrange
        string hex = "01 03 00 00 00 0A C5 CD";

        // Act
        byte[] bytes = ByteUtils.HexToBytes(hex);

        // Assert
        byte[] expected = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD];
        CollectionAssert.AreEqual(expected, bytes);
    }

    [TestMethod]
    public void HexToBytes_ContinuousAndMixedCase_ParsesAccurately()
    {
        // Arrange
        string hex = "01030000000ac5cd";

        // Act
        byte[] bytes = ByteUtils.HexToBytes(hex);

        // Assert
        byte[] expected = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD];
        CollectionAssert.AreEqual(expected, bytes);
    }

    [TestMethod]
    public void HexToBytes_With0xPrefixAndPunctuation_ParsesAccurately()
    {
        // Arrange
        string hex = "0x01, 0x03, 0x00, 0x0A";

        // Act
        byte[] bytes = ByteUtils.HexToBytes(hex);

        // Assert
        byte[] expected = [0x01, 0x03, 0x00, 0x0A];
        CollectionAssert.AreEqual(expected, bytes);
    }

    [TestMethod]
    public void HexToBytes_EmptyAndWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, ByteUtils.HexToBytes("").Length);
        Assert.AreEqual(0, ByteUtils.HexToBytes("   ").Length);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void HexToBytes_Null_ThrowsArgumentNullException()
    {
        _ = ByteUtils.HexToBytes(null!);
    }

    [TestMethod]
    [ExpectedException(typeof(FormatException))]
    public void HexToBytes_OddLength_ThrowsFormatException()
    {
        _ = ByteUtils.HexToBytes("01 03 0");
    }

    [TestMethod]
    [ExpectedException(typeof(FormatException))]
    public void HexToBytes_InvalidCharacter_ThrowsFormatException()
    {
        _ = ByteUtils.HexToBytes("01 03 00 0G");
    }

    [TestMethod]
    public void BytesToHex_StandardFormat_MatchesExpectedString()
    {
        byte[] bytes = [0x01, 0x03, 0x00, 0x0A, 0xC5, 0xCD];
        string hex = ByteUtils.BytesToHex(bytes);

        Assert.AreEqual("01 03 00 0A C5 CD", hex);
    }

    [TestMethod]
    public void AppendDelimiter_AllOptions_AppendsCorrectly()
    {
        byte[] payload = [0x31, 0x32, 0x33]; // "123"

        // None
        CollectionAssert.AreEqual(payload, ByteUtils.AppendDelimiter(payload, EndingDelimiter.None));

        // CR (0x0D)
        byte[] crExpected = [0x31, 0x32, 0x33, 0x0D];
        CollectionAssert.AreEqual(crExpected, ByteUtils.AppendDelimiter(payload, EndingDelimiter.CR));

        // LF (0x0A)
        byte[] lfExpected = [0x31, 0x32, 0x33, 0x0A];
        CollectionAssert.AreEqual(lfExpected, ByteUtils.AppendDelimiter(payload, EndingDelimiter.LF));

        // CRLF (0x0D, 0x0A)
        byte[] crlfExpected = [0x31, 0x32, 0x33, 0x0D, 0x0A];
        CollectionAssert.AreEqual(crlfExpected, ByteUtils.AppendDelimiter(payload, EndingDelimiter.CRLF));
    }

    [TestMethod]
    public void EncodeInput_AsciiAndUtf8_EncodesAccurately()
    {
        string text = "Hello 工业";

        byte[] asciiBytes = ByteUtils.EncodeInput("Hello", DataEncoding.Ascii, EndingDelimiter.CRLF);
        byte[] expectedAscii = [0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0D, 0x0A];
        CollectionAssert.AreEqual(expectedAscii, asciiBytes);

        byte[] utf8Bytes = ByteUtils.EncodeInput(text, DataEncoding.Utf8, EndingDelimiter.None);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(text), utf8Bytes);
    }
}
