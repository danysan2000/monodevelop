﻿//
// CodeIssueDescriptor.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using Microsoft.CodeAnalysis.Diagnostics;
using MonoDevelop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using MonoDevelop.Ide.Editor;

namespace MonoDevelop.CodeIssues
{
	class CodeDiagnosticDescriptor
	{
		readonly Type diagnosticAnalyzerType;
		readonly Microsoft.CodeAnalysis.DiagnosticDescriptor descriptor;

		DiagnosticAnalyzer instance;


		/// <summary>
		/// Gets the identifier string.
		/// </summary>
		internal string IdString {
			get {
				return diagnosticAnalyzerType.FullName;
			}
		}

		public Type DiagnosticAnalyzerType {
			get {
				return diagnosticAnalyzerType;
			}
		}

		/// <summary>
		/// Gets the display name for this issue.
		/// </summary>
		public string Name { get; private set; }

		/// <summary>
		/// Gets the description of the issue provider (used in the option panel).
		/// </summary>

		/// <summary>
		/// Gets the languages for this issue.
		/// </summary>
		public string[] Languages { get; private set; }

		public DiagnosticSeverity? DiagnosticSeverity {
			get {
				DiagnosticSeverity? result = null;

				foreach (var diagnostic in GetProvider ().SupportedDiagnostics) {
					if (!result.HasValue)
						result = GetSeverity(diagnostic);
					if (result != GetSeverity(diagnostic))
						return null;
				}
				return result;
			}

			set {
				if (!value.HasValue)
					return;
				foreach (var diagnostic in GetProvider ().SupportedDiagnostics) {
					SetSeverity (diagnostic, value.Value);
				}
			}
		}

		/// <summary>
		/// Gets or sets a value indicating whether this code action is enabled by the user.
		/// </summary>
		/// <value><c>true</c> if this code action is enabled; otherwise, <c>false</c>.</value>
		public bool IsEnabled {
			get {
				foreach (var diagnostic in GetProvider ().SupportedDiagnostics) {
					if (!GetIsEnabled (diagnostic))
						return false;
				}
				return true;
			}
			set {
				foreach (var diagnostic in GetProvider ().SupportedDiagnostics) {
					SetIsEnabled (diagnostic, value);
				}
			}
		}

		internal DiagnosticSeverity GetSeverity (DiagnosticDescriptor diagnostic)
		{
			return PropertyService.Get ("CodeIssues." + Languages + "." + IdString + "." + diagnostic.Id + ".severity", diagnostic.DefaultSeverity);
		}

		internal void SetSeverity (DiagnosticDescriptor diagnostic, DiagnosticSeverity severity)
		{
			PropertyService.Set ("CodeIssues." + Languages + "." + IdString + "." + diagnostic.Id + ".severity", severity);
		}

		internal bool GetIsEnabled (DiagnosticDescriptor diagnostic)
		{
			return PropertyService.Get ("CodeIssues." + Languages + "." + IdString + "." + diagnostic.Id + ".enabled", true);
		}

		internal void SetIsEnabled (DiagnosticDescriptor diagnostic, bool value)
		{
			PropertyService.Set ("CodeIssues." + Languages + "." + IdString + "." + diagnostic.Id + ".enabled", value);
		}

		internal CodeDiagnosticDescriptor (Microsoft.CodeAnalysis.DiagnosticDescriptor descriptor, string[] languages, Type codeIssueType)
		{
			if (descriptor == null)
				throw new ArgumentNullException ("descriptor");
			if (languages == null)
				throw new ArgumentNullException ("languages");
			if (codeIssueType == null)
				throw new ArgumentNullException ("codeIssueType");
			Name = descriptor.Title.ToString () ?? "unnamed";
			Languages = languages;
			this.descriptor = descriptor;
			this.diagnosticAnalyzerType = codeIssueType;
		}

		/// <summary>
		/// Gets the roslyn code action provider.
		/// </summary>
		public DiagnosticAnalyzer GetProvider ()
		{
			if (instance == null)
				instance = (DiagnosticAnalyzer)Activator.CreateInstance(diagnosticAnalyzerType);

			return instance;
		}

		public override string ToString ()
		{
			return string.Format ("[CodeIssueDescriptor: IdString={0}, Name={1}, Language={2}]", IdString, Name, Languages);
		}

		public bool CanDisableWithPragma { get { return !string.IsNullOrEmpty (descriptor.Id); } }

		const string analysisDisableTag = "Analysis ";

		public void DisableWithPragma (TextEditor editor, DocumentContext context, TextSpan span)
		{
			using (editor.OpenUndoGroup ()) {
				var start = editor.OffsetToLocation (span.Start);
				var end = editor.OffsetToLocation (span.End);
				editor.InsertText (
					editor.LocationToOffset (end.Line + 1, 1),
					editor.GetVirtualIndentationString (end.Line) + "#pragma warning restore " + descriptor.Id + editor.EolMarker
				); 
				editor.InsertText (
					editor.LocationToOffset (start.Line, 1),
					editor.GetVirtualIndentationString (start.Line) + "#pragma warning disable " + descriptor.Id + editor.EolMarker
				); 
			}
		}
	}
}

