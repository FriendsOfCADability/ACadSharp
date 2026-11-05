using ACadSharp.Entities;
using CSMath;
using System;
using System.Collections.Generic;
using Xunit;

namespace ACadSharp.Tests.Entities;

public class MTextTests : CommonEntityTests<MText>
{
	public override void GetBoundingBoxTest()
	{
		XYZ pt = this._random.NextXYZ();
		MText text = new MText("hello") { InsertPoint = pt };

		var box = text.GetBoundingBox();
		Assert.Equal(BoundingBoxExtent.Point, box.Extent);
		Assert.Equal(pt, box.Center);
	}

	[Fact]
	public void PlainTextTest()
	{
		var s = Text.TextProcessor.Parse("- Font: {\\fCalibri|b0|i0|c0|p34;Calibri\\Fcdm|c0; CDM \\fConsolas|b0|i0|c0|p49;Consolas\\P}", out List<string> groups);
		Assert.Equal($"- Font: Calibri CDM Consolas{Environment.NewLine}", s);
		//Assert.Equal("- Font: ", groups[0]);
		//Assert.Equal("Calibri", groups[1]);

		MText text = new MText("- Font: {\\fCalibri|b0|i0|c0|p34;Calibri\\Fcdm|c0; CDM \\fConsolas|b0|i0|c0|p49;Consolas\\P}");
		Assert.Equal($"- Font: Calibri CDM Consolas{Environment.NewLine}", text.PlainText);

		text = new MText("- Color text {\\C3;green}, {\\C5;blue}, {\\C1;red}, ByLayer, {\\C0;ByBlock}, {\\C21;\\c5872631;TrueColor}");
		Assert.Equal("- Color text green, blue, red, ByLayer, ByBlock, TrueColor", text.PlainText);

		text = new MText("- {\\H2x;Double height \\H0.875x;height is: 0.35\\H1.14286x;\\P}");
		Assert.Equal($"- Double height height is: 0.35{Environment.NewLine}", text.PlainText);

		text = new MText("- {\\C4;Text in \\fCalibri|b0|i0|c0|p34;Calibri and cyan\\P}");
		Assert.Equal($"- Text in Calibri and cyan{Environment.NewLine}", text.PlainText);

		text = new MText("\\pxqc;Text in the center\\P\\pq*;Hello this is an mText\\P");
		Assert.Equal($"Text in the center{Environment.NewLine}Hello this is an mText{Environment.NewLine}", text.PlainText);

		text = new MText("{\\fArial|b1|i0|c0|p34;Bold Text \\fArial|b0|i1|c0|p34;Italic Text \\P}");
		Assert.Equal($"Bold Text Italic Text {Environment.NewLine}", text.PlainText);

		text = new MText("Special characters : \\{ \\} * \\\\ ~ % & ( ) ? ¿ ! ¡");
		Assert.Equal(@"Special characters : { } * \ ~ % & ( ) ? ¿ ! ¡", text.PlainText);
	}

	[Theory]
	[InlineData("abc\\H2.5x", "abc")]
	[InlineData("\\H2.5x", "")]
	[InlineData("{\\C1red}", "")]
	[InlineData("x\\A1", "x")]
	[InlineData("x\\pxqc", "x")]
	[InlineData("y\\fArial|b1", "y")]
	[InlineData("C:\\Users\\hans", "C:Users")]
	public void PlainTextOfFormatCodesWithoutSemicolon(string value, string expected)
	{
		// a format code that is not terminated by ';' runs to the end of the text; it used to restart the parsing at index 0,
		// an endless loop that also filled the string builder until the memory was exhausted
		Assert.Equal(expected, new MText(value).PlainText);
	}

	[Theory]
	[InlineData("\\H2x;text", "\\H2x;text")]
	[InlineData("a\\rb", "ab")]
	[InlineData("a\\{b\\}", "a{b}")]
	[InlineData("\\Pnext", "\nnext")]
	[InlineData("{\\C1;red} and {\\fArial|b1;bold}", "red and bold")]
	[InlineData("ends with \\", "ends with \\")]
	[InlineData("C:\\Users\\hans", "C:\\Users\\hans")]
	public void UnescapeEndsForEveryCode(string value, string expected)
	{
		// codes that Unescape does not remove used to keep the index on the backslash, an endless loop
		Assert.Equal(expected.Replace("\n", Environment.NewLine), Text.TextProcessor.Unescape(value));
	}
}
