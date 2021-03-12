﻿using MiniExcelLibs.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MiniExcelLibs.OpenXml
{
    internal class ExcelOpenXmlSheetReader
    {
	   internal Dictionary<int, string> GetSharedStrings(ZipArchiveEntry sharedStringsEntry)
	   {
		  var xl = XElement.Load(sharedStringsEntry.Open());
		  var ts = xl.Descendants(ExcelOpenXmlXName.T).Select((s, i) => new { i, v = s.Value?.ToString() })
			   .ToDictionary(s => s.i, s => s.v)
		  ;
		  return ts;
	   }

	   private static Dictionary<int, string> _SharedStrings;

	   internal IEnumerable<object> QueryImpl(Stream stream, bool UseHeaderRow = false)
	   {
		  using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false, UTF8Encoding.UTF8))
		  {
			 var e = archive.Entries.SingleOrDefault(w => w.FullName == "xl/sharedStrings.xml");
			 _SharedStrings = GetSharedStrings(e);

			 var firstSheetEntry = archive.Entries.First(w => w.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase));
			 using (var firstSheetEntryStream = firstSheetEntry.Open())
			 {
				using (XmlReader reader = XmlReader.Create(firstSheetEntryStream, XmlSettings))
				{
				    var ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
				    if (!reader.IsStartElement("worksheet", ns))
					   yield break;


				    if (!XmlReaderHelper.ReadFirstContent(reader))
					   yield break;

				    var maxRowIndex = -1;
				    var maxColumnIndex = -1;
				    while (!reader.EOF)
				    {
					   //TODO: will dimension after sheetData?
					   //this method logic depends on dimension to get maxcolumnIndex, if without dimension then it need to foreach all rows first time to get maxColumn and maxRowColumn
					   if (reader.IsStartElement("dimension", ns))
					   {
						  var @ref = reader.GetAttribute("ref");
						  if (string.IsNullOrEmpty(@ref))
							 throw new InvalidOperationException("Without sheet dimension data");
						  var rs = @ref.Split(':');
						  if (ReferenceHelper.ParseReference(rs[1], out int cIndex, out int rIndex))
						  {
							 maxColumnIndex = cIndex - 1;
							 maxRowIndex = rIndex - 1;
						  }
						  else
							 throw new InvalidOperationException("Invaild sheet dimension start data");
					   }
					   if (reader.IsStartElement("sheetData", ns))
					   {
						  if (!XmlReaderHelper.ReadFirstContent(reader))
						  {
							 continue;
						  }

						  Dictionary<int, string> headRows = new Dictionary<int, string>();
						  int rowIndex = -1;
						  int nextRowIndex = 0;
						  while (!reader.EOF)
						  {
							 if (reader.IsStartElement("row", ns))
							 {
								nextRowIndex = rowIndex + 1;
								if (int.TryParse(reader.GetAttribute("r"), out int arValue))
								    rowIndex = arValue - 1; // The row attribute is 1-based
								else
								    rowIndex++;
								if (!XmlReaderHelper.ReadFirstContent(reader))
								    continue;

								// fill empty rows
								{
								    if (nextRowIndex < rowIndex)
								    {
									   for (int i = nextRowIndex; i < rowIndex; i++)
										  if (UseHeaderRow)
											 yield return Helpers.GetEmptyExpandoObject(headRows);
										  else
											 yield return Helpers.GetEmptyExpandoObject(maxColumnIndex);
								    }
								}

								// Set Cells
								{
								    var cell = UseHeaderRow ? Helpers.GetEmptyExpandoObject(headRows) : Helpers.GetEmptyExpandoObject(maxColumnIndex);
								    var columnIndex = 0;
								    while (!reader.EOF)
								    {
									   if (reader.IsStartElement("c", ns))
									   {
										  var cellValue = ReadCell(reader, columnIndex, out var _columnIndex);
										  columnIndex = _columnIndex;

										  //if not using First Head then using 1,2,3 as index
										  if (UseHeaderRow)
										  {
											 if (rowIndex == 0)
												headRows.Add(columnIndex, cellValue.ToString());
											 else
												cell[headRows[columnIndex]] = cellValue;
										  }
										  else
											 cell[columnIndex.ToString()] = cellValue;
									   }
									   else if (!XmlReaderHelper.SkipContent(reader))
										  break;
								    }

								    if (UseHeaderRow && rowIndex == 0)
									   continue;

								    yield return cell;
								}
							 }
							 else if (!XmlReaderHelper.SkipContent(reader))
							 {
								break;
							 }
						  }

					   }
					   else if (!XmlReaderHelper.SkipContent(reader))
					   {
						  break;
					   }
				    }
				}
			 }
		  }
	   }

	   private object ReadCell(XmlReader reader, int nextColumnIndex, out int columnIndex)
	   {
		  var aT = reader.GetAttribute("t");
		  var aR = reader.GetAttribute("r");

		  //TODO:need to check only need nextColumnIndex or columnIndex
		  if (ReferenceHelper.ParseReference(aR, out int referenceColumn, out _))
			 columnIndex = referenceColumn - 1; // ParseReference is 1-based
		  else
			 columnIndex = nextColumnIndex;

		  if (!XmlReaderHelper.ReadFirstContent(reader))
			 return null;


		  object value = null;
		  while (!reader.EOF)
		  {
			 if (reader.IsStartElement("v", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))
			 {
				string rawValue = reader.ReadElementContentAsString();
				if (!string.IsNullOrEmpty(rawValue))
				    ConvertCellValue(rawValue, aT, out value);
			 }
			 else if (reader.IsStartElement("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))
			 {
				string rawValue = StringHelper.ReadStringItem(reader);
				if (!string.IsNullOrEmpty(rawValue))
				    ConvertCellValue(rawValue, aT, out value);
			 }
			 else if (!XmlReaderHelper.SkipContent(reader))
			 {
				break;
			 }
		  }
		  return value;
	   }

	   private void ConvertCellValue(string rawValue, string aT, out object value)
	   {
		  const NumberStyles style = NumberStyles.Any;
		  var invariantCulture = CultureInfo.InvariantCulture;

		  switch (aT)
		  {
			 case "s": //// if string
				if (int.TryParse(rawValue, style, invariantCulture, out var sstIndex))
				{
				    if (_SharedStrings.ContainsKey(sstIndex))
					   value = _SharedStrings[sstIndex];
				    else
					   value = sstIndex;
				    return;
				}

				value = rawValue;
				return;
			 case "inlineStr": //// if string inline
			 case "str": //// if cached formula string
				value = Helpers.ConvertEscapeChars(rawValue);
				return;
			 case "b": //// boolean
				value = rawValue == "1";
				return;
			 case "d": //// ISO 8601 date
				if (DateTime.TryParseExact(rawValue, "yyyy-MM-dd", invariantCulture, DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite, out var date))
				{
				    value = date;
				    return;
				}

				value = rawValue;
				return;
			 case "e": //// error
				value = rawValue;
				return;
			 default:
				if (double.TryParse(rawValue, style, invariantCulture, out double number))
				{
				    value = number;
				    return;
				}

				value = rawValue;
				return;
		  }
	   }

	   private static readonly XmlReaderSettings XmlSettings = new XmlReaderSettings
	   {
		  IgnoreComments = true,
		  IgnoreWhitespace = true,
		  XmlResolver = null,
	   };
    }

}
