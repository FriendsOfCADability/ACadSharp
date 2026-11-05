using CSUtilities.Extensions;
using System;
using System.Collections.Generic;
using System.Text;

namespace ACadSharp.Text
{
	public static class TextProcessor
	{
		public static string Parse(string text, out List<string> groups)
		{
			groups = new List<string>();
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}

			StringBuilder sb = new StringBuilder();

			int index = 0;
			bool openGroup = false;
			while (index < text.Length)
			{
				int start = index;
				char? prev = text.TryGet(index - 1);
				char? current = text.TryGet(index);
				char? next = text.TryGet(index + 1);

				if (current == '\\' && next.HasValue)
				{
					switch (next)
					{
						case '}':
						case '{':
						case '\\':
							sb.Append(next);
							index += 2;
							break;
						case 'A':
							jump(text, index, out index);
							break;
						case 'c':
						case 'C':
							processColor(text, index, out index);
							break;
						case 'f':
						case 'F':
							processFont(text, index, out index);
							break;
						case 'h':
						case 'H':
							processHeight(text, index, out index);
							break;
						case 'p':
							processJustification(text, index, out index);
							break;
						case 'P':
						case 'n':
							sb.Append(Environment.NewLine);
							index += 2;
							break;
						default:
							index++;
							break;
					}
				}
				else if (current == '{' && prev != '\\')
				{
					openGroup = true;
					index++;
				}
				else if (current == '}' && prev != '\\')
				{
					openGroup = false;
					index++;
				}
				else
				{
					sb.Append(current);
					index++;
				}

				// every step must advance, a format code without its terminating ';' must not restart the loop
				if (index <= start)
				{
					index = start + 1;
				}
			}

			return sb.ToString();
		}


		public static string Unescape(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}

			StringBuilder sb = new StringBuilder();

			int index = 0;
			bool openGroup = false;
			while (index < text.Length)
			{
				int codeIndex = text.IndexOf('\\', index);
				if (codeIndex < 0 || codeIndex == text.Length - 1)
				{
					// no more escape sequences, a backslash at the very end is plain text
					appendText(sb, text.Substring(index), ref openGroup);
					break;
				}

				int textEnd = codeIndex;
				bool groupStart = codeIndex > index && text[codeIndex - 1] == '{';
				if (groupStart)
				{
					textEnd--;
				}

				if (textEnd > index)
				{
					appendText(sb, text.Substring(index, textEnd - index), ref openGroup);
				}

				if (groupStart)
				{
					openGroup = true;
				}

				char next = text[codeIndex + 1];
				int end;
				switch (next)
				{
					case 'f':
					case 'F':
						processFont(text, codeIndex, out end);
						break;
					case 'c':
					case 'C':
						processColor(text, codeIndex, out end);
						break;
					case 'P':
					case 'n':
						sb.Append(Environment.NewLine);
						end = codeIndex + 2;
						break;
					case '}':
					case '{':
					case '\\':
						sb.Append(next);
						end = codeIndex + 2;
						break;
					case 'r':
						end = codeIndex + 2;
						break;
					default:
						// not a code this method removes, e.g. a backslash in a path: kept as it is
						sb.Append('\\').Append(next);
						end = codeIndex + 2;
						break;
				}

				// every step must advance
				index = Math.Max(end, codeIndex + 2);
			}

			return sb.ToString();
		}

		private static void appendText(StringBuilder sb, string s, ref bool openGroup)
		{
			if (openGroup && s.Contains("}"))
			{
				s = s.Replace("}", string.Empty);
				openGroup = false;
			}

			sb.Append(s);
		}

		/// <summary>
		/// The position after the ';' that terminates the format code at <paramref name="start"/>, the end of the text when
		/// the ';' is missing.
		/// </summary>
		private static int formatCodeEnd(string text, int start)
		{
			int end = text.IndexOf(';', start);
			return end < 0 ? text.Length : end + 1;
		}

		private static void processFont(string text, int start, out int end)
		{
			//Example:
			//- Font: {\\fCalibri|b0|i0|c0|p34;Calibri\\Fcdm|c0; CDM \\fConsolas|b0|i0|c0|p49;Consolas\\P}
			end = formatCodeEnd(text, start);
			var data = text.Substring(start, end - start).Split('|');

			FontData fontData = new FontData
			{
				Name = data[0],
			};
		}

		private static void jump(string text, int start, out int end)
		{
			//Example:
			//\\(unknown escape code)(escape text);
			end = formatCodeEnd(text, start);
		}

		private static void processColor(string text, int start, out int end)
		{
			//Example:
			//- Color text {\\C3;green}, {\\C5;blue}, {\\C1;red}, ByLayer, {\\C0;ByBlock}, {\\C21;\\c5872631;TrueColor}
			end = formatCodeEnd(text, start);
		}

		private static void processHeight(string text, int start, out int end)
		{
			//Example:
			//- {\\H2x;Double height \\H0.875x;height is: 0.35\\H1.14286x;\\P}
			end = formatCodeEnd(text, start);
		}

		private static void processJustification(string text, int start, out int end)
		{
			//Example:
			//\\pxqc;Text in the center\\P\\pq*;Hello this is an mText\\P
			end = formatCodeEnd(text, start);
		}
	}

	internal struct FontData
	{
		public string Name { get; set; }
	}
}
