﻿//
// RazorCSharpParser.cs
//
// Author:
//		Piotr Dowgiallo <sparekd@gmail.com>
//
// Copyright (c) 2012 Piotr Dowgiallo
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
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Configuration;
using System.Web.Mvc.Razor;
using System.Web.Razor;
using System.Web.Razor.Parser;
using System.Web.Razor.Parser.SyntaxTree;
using System.Web.Razor.Text;
using System.Web.WebPages.Razor;
using System.Web.WebPages.Razor.Configuration;


using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.Projects;
using MonoDevelop.AspNet.Projects;
using MonoDevelop.AspNet.WebForms.Parser;
using MonoDevelop.AspNet.Razor.Parser;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Core.Text;

namespace MonoDevelop.AspNet.Razor
{
	// TODO: Roslyn - Fix threading issues with using member variables.
	public class RazorCSharpParser : TypeSystemParser
	{
		MonoDevelop.Web.Razor.EditorParserFixed.RazorEditorParser editorParser;
		DocumentParseCompleteEventArgs capturedArgs;
		AutoResetEvent parseComplete;
		ChangeInfo lastChange;
		string lastParsedFile;
		MonoDevelop.Ide.Editor.ITextDocument currentDocument;
		AspNetAppProjectFlavor aspProject;
		DotNetProject project;
		IList<MonoDevelop.Ide.Editor.ITextDocument> openDocuments;

		public IList<MonoDevelop.Ide.Editor.ITextDocument> OpenDocuments { get { return openDocuments; } }

		public RazorCSharpParser ()
		{
			openDocuments = new List<MonoDevelop.Ide.Editor.ITextDocument> ();

			IdeApp.Exited += delegate {
				//HACK: workaround for Mono's not shutting downs IsBackground threads in WaitAny calls
				if (editorParser != null) {
					DisposeCurrentParser ();
				}
			};
		}

		public override System.Threading.Tasks.Task<ParsedDocument> Parse (MonoDevelop.Ide.TypeSystem.ParseOptions parseOptions, CancellationToken cancellationToken)
		{
			currentDocument = openDocuments.FirstOrDefault (d => d != null && d.FileName == parseOptions.FileName);
			// We need document and project to be loaded to correctly initialize Razor Host.
			this.project = parseOptions.Project as DotNetProject;
			if (currentDocument == null && !TryAddDocument (parseOptions.FileName))
				return System.Threading.Tasks.Task.FromResult((ParsedDocument)new RazorCSharpParsedDocument (parseOptions.FileName, new RazorCSharpPageInfo ()));

			this.aspProject = project.GetService<AspNetAppProjectFlavor> ();

			EnsureParserInitializedFor (parseOptions.FileName);

			var errors = new List<Error> ();

			using (var source = new SeekableTextReader (parseOptions.Content.CreateReader ())) {
				var textChange = CreateTextChange (source);
				var parseResult = editorParser.CheckForStructureChanges (textChange);
				if (parseResult == PartialParseResult.Rejected) {
					parseComplete.WaitOne ();
					if (!capturedArgs.GeneratorResults.Success)
						GetRazorErrors (errors);
				}
			}

			ParseHtmlDocument (errors);
			CreateCSharpParsedDocument (parseOptions);
			ClearLastChange ();

			RazorHostKind kind = RazorHostKind.WebPage;
			if (editorParser.Host is WebCodeRazorHost) {
				kind = RazorHostKind.WebCode;
			} else if (editorParser.Host is MonoDevelop.AspNet.Razor.Generator.PreprocessedRazorHost) {
				kind = RazorHostKind.Template;
			}

			var model = document.GetSemanticModelAsync (cancellationToken).Result;
			var pageInfo = new RazorCSharpPageInfo () {
				HtmlRoot = htmlParsedDocument,
				GeneratorResults = capturedArgs.GeneratorResults,
				Spans = editorParser.CurrentParseTree.Flatten (),
				CSharpSyntaxTree = parsedSyntaxTree,
				ParsedDocument = new DefaultParsedDocument ("generated.cs") { Ast = model },
				AnalysisDocument = document,
				CSharpCode = csharpCode,
				Errors = errors,
				FoldingRegions = GetFoldingRegions (),
				Comments = comments,
				HostKind = kind,
			};

			return System.Threading.Tasks.Task.FromResult((ParsedDocument)new RazorCSharpParsedDocument (parseOptions.FileName, pageInfo));
		}

		bool TryAddDocument (string fileName)
		{
			if (string.IsNullOrEmpty (fileName))
				return false;

			var guiDoc = IdeApp.Workbench.GetDocument (fileName);
			if (guiDoc != null && guiDoc.Editor != null) {
				currentDocument = guiDoc.Editor;
				currentDocument.TextChanging += OnTextReplacing;
				lock (this) {
					var newDocs = new List<MonoDevelop.Ide.Editor.ITextDocument> (openDocuments);
					newDocs.Add (currentDocument);
					openDocuments = newDocs;
				}
				guiDoc.Closed += (sender, args) =>
				{
					var doc = sender as MonoDevelop.Ide.Gui.Document;
					if (doc.Editor != null && doc.Editor != null) {
						lock (this) {
							openDocuments = new List<MonoDevelop.Ide.Editor.ITextDocument> (openDocuments.Where (d => d.FileName != doc.Editor.FileName));
						}
					}

					if (lastParsedFile == doc.FileName && editorParser != null) {
						DisposeCurrentParser ();
					}
				};
				return true;
			}
			return false;
		}

		void EnsureParserInitializedFor (string fileName)
		{
			if (lastParsedFile == fileName && editorParser != null)
				return;

			if (editorParser != null)
				DisposeCurrentParser ();

			CreateParserFor (fileName);
		}

		void CreateParserFor (string fileName)
		{
			editorParser = new MonoDevelop.Web.Razor.EditorParserFixed.RazorEditorParser (CreateRazorHost (fileName), fileName);

			parseComplete = new AutoResetEvent (false);
			editorParser.DocumentParseComplete += (sender, args) =>
			{
				capturedArgs = args;
				parseComplete.Set ();
			};

			lastParsedFile = fileName;
		}

		RazorEngineHost CreateRazorHost (string fileName)
		{
			if (project != null) {
				var projectFile = project.GetProjectFile (fileName);
				if (projectFile != null && projectFile.Generator == "RazorTemplatePreprocessor") {
					return new MonoDevelop.AspNet.Razor.Generator.PreprocessedRazorHost (fileName) {
						DesignTimeMode = true,
						EnableLinePragmas = false,
					};
				}
			}

			string virtualPath = "~/Views/Default.cshtml";
			if (aspProject != null)
				virtualPath = aspProject.LocalToVirtualPath (fileName);

			WebPageRazorHost host = null;

			// Try to create host using web.config file
			var webConfigMap = new WebConfigurationFileMap ();
			if (aspProject != null) {
				var vdm = new VirtualDirectoryMapping (project.BaseDirectory.Combine ("Views"), true, "web.config");
			webConfigMap.VirtualDirectories.Add ("/", vdm);
			}
			Configuration configuration;
			try {
				configuration = WebConfigurationManager.OpenMappedWebConfiguration (webConfigMap, "/");
			} catch {
				configuration = null;
			}
			if (configuration != null) {
				//TODO: use our assemblies, not the project's
				var rws = configuration.GetSectionGroup (RazorWebSectionGroup.GroupName) as RazorWebSectionGroup;
				if (rws != null) {
					host = WebRazorHostFactory.CreateHostFromConfig (rws, virtualPath, fileName);
					host.DesignTimeMode = true;
				}
			}

			if (host == null) {
				host = new MvcWebPageRazorHost (virtualPath, fileName) { DesignTimeMode = true };
				// Add default namespaces from Razor section
				host.NamespaceImports.Add ("System.Web.Mvc");
				host.NamespaceImports.Add ("System.Web.Mvc.Ajax");
				host.NamespaceImports.Add ("System.Web.Mvc.Html");
				host.NamespaceImports.Add ("System.Web.Routing");
			}

			return host;
		}

		void DisposeCurrentParser ()
		{
			editorParser.Dispose ();
			editorParser = null;
			parseComplete.Dispose ();
			parseComplete = null;
			ClearLastChange ();
		}

		void ClearLastChange ()
		{
			lastChange = null;
		}

		TextChange CreateTextChange (SeekableTextReader source)
		{
			if (lastChange == null)
				return new TextChange (0, 0, new SeekableTextReader (String.Empty), 0, source.Length, source);
			if (lastChange.DeleteChange)
				return new TextChange (lastChange.StartOffset, lastChange.AbsoluteLength, lastChange.Buffer,
					lastChange.StartOffset,	0, source);
			return new TextChange (lastChange.StartOffset, 0, lastChange.Buffer, lastChange.StartOffset,
				lastChange.AbsoluteLength, source);
		}

		void GetRazorErrors (List<Error> errors)
		{
			foreach (var error in capturedArgs.GeneratorResults.ParserErrors) {
				int off = error.Location.AbsoluteIndex;
				if (error.Location.CharacterIndex > 0 && error.Length == 1)
					off--;
				errors.Add (new Error (ErrorType.Error, error.Message, currentDocument.OffsetToLocation (off)));
			}
		}

		MonoDevelop.Xml.Dom.XDocument htmlParsedDocument;
		IList<Comment> comments;

		void ParseHtmlDocument (List<Error> errors)
		{
			var sb = new StringBuilder ();
			var spanList = new List<Span> ();
			comments = new List<Comment> ();

			Action<Span> action = (Span span) =>
			{
				if (span.Kind == SpanKind.Markup) {
					sb.Append (span.Content);
					spanList.Add (span);
				} else {
					for (int i = 0; i < span.Content.Length; i++) {
						char ch = span.Content[i];
						if (ch != '\r' && ch != '\n')
							sb.Append (' ');
						else
							sb.Append (ch);
					}
					if (span.Kind == SpanKind.Comment) {
						var comment = new Comment (span.Content)
						{
							OpenTag = "@*",
							ClosingTag = "*@",
							CommentType = CommentType.Block,
						};
						comment.Region = new MonoDevelop.Ide.Editor.DocumentRegion (
							currentDocument.OffsetToLocation (span.Start.AbsoluteIndex - comment.OpenTag.Length),
							currentDocument.OffsetToLocation (span.Start.AbsoluteIndex + span.Length + comment.ClosingTag.Length));
						comments.Add (comment);
					}
				}
			};

			editorParser.CurrentParseTree.Accept (new CallbackVisitor (action));

			var parser = new MonoDevelop.Xml.Parser.XmlParser (new WebFormsRootState (), true);

			try {
				parser.Parse (new StringReader (sb.ToString ()));
			} catch (Exception ex) {
				LoggingService.LogError ("Unhandled error parsing html in Razor document '" + (lastParsedFile ?? "") + "'", ex);
			}

			htmlParsedDocument = parser.Nodes.GetRoot ();
			errors.AddRange (parser.Errors);
		}

		IEnumerable<FoldingRegion> GetFoldingRegions ()
		{
			var foldingRegions = new List<FoldingRegion> ();
			GetHtmlFoldingRegions (foldingRegions);
			GetRazorFoldingRegions (foldingRegions);
			return foldingRegions;
		}

		void GetHtmlFoldingRegions (List<FoldingRegion> foldingRegions)
		{
			if (htmlParsedDocument != null) {
				var d = new MonoDevelop.AspNet.WebForms.WebFormsParsedDocument (null, WebSubtype.Html, null, htmlParsedDocument);
				foldingRegions.AddRange (d.Foldings);
			}
		}

		void GetRazorFoldingRegions (List<FoldingRegion> foldingRegions)
		{
			var blocks = new List<Block> ();
			GetBlocks (editorParser.CurrentParseTree, blocks);
			foreach (var block in blocks) {
				var beginLine = currentDocument.GetLineByOffset (block.Start.AbsoluteIndex);
				var endLine = currentDocument.GetLineByOffset (block.Start.AbsoluteIndex + block.Length);
				if (beginLine != endLine)
					foldingRegions.Add (new FoldingRegion (RazorUtils.GetShortName (block),
						new DocumentRegion (currentDocument.OffsetToLocation (block.Start.AbsoluteIndex),
							currentDocument.OffsetToLocation (block.Start.AbsoluteIndex + block.Length))));
			}
		}

		void GetBlocks (Block root, IList<Block> blocks)
		{
			foreach (var block in root.Children.Where (n => n.IsBlock).Select (n => n as Block)) {
				if (block.Type != BlockType.Comment && block.Type != BlockType.Markup)
					blocks.Add (block);
				if (block.Type != BlockType.Helper)
					GetBlocks (block, blocks);
			}
		}

		SyntaxTree parsedSyntaxTree;
		string csharpCode;
		Microsoft.CodeAnalysis.Document document;

		void CreateCSharpParsedDocument (MonoDevelop.Ide.TypeSystem.ParseOptions parseOptions)
		{
			if (parseOptions.Project == null)
				return;

			csharpCode = CreateCodeFile ();
			parsedSyntaxTree = CSharpSyntaxTree.ParseText (Microsoft.CodeAnalysis.Text.SourceText.From (csharpCode));

			var originalProject = TypeSystemService.GetCodeAnalysisProject (parseOptions.Project);
			if (originalProject != null) {
				string fileName = parseOptions.FileName + ".g.cs";
				var documentId = TypeSystemService.GetDocumentId (originalProject.Id, fileName);
				if (documentId == null) {
					document = originalProject.AddDocument (
						fileName,
						parsedSyntaxTree?.GetRoot ());
				} else {
					document = TypeSystemService.GetCodeAnalysisDocument (documentId);
				}
			}
		}

		string CreateCodeFile ()
		{
			var unit = capturedArgs.GeneratorResults.GeneratedCode;
			System.CodeDom.Compiler.CodeDomProvider provider = project != null
				? project.LanguageBinding.GetCodeDomProvider ()
				: new Microsoft.CSharp.CSharpCodeProvider ();
			using (var sw = new StringWriter ()) {
				provider.GenerateCodeFromCompileUnit (unit, sw, new System.CodeDom.Compiler.CodeGeneratorOptions ()	{
					// HACK: we use true, even though razor uses false, to work around a mono bug where it omits the 
					// line ending after "#line hidden", resulting in the unparseable "#line hiddenpublic"
					BlankLinesBetweenMembers = true,
					// matches Razor built-in settings
					IndentString = String.Empty,
				});
				return sw.ToString ();
			}
		}

		void OnTextReplacing (object sender, MonoDevelop.Core.Text.TextChangeEventArgs e)
		{
			if (lastChange == null)
				lastChange = new ChangeInfo (e.Offset, new SeekableTextReader((sender as MonoDevelop.Ide.Editor.ITextDocument).Text));
			if (e.ChangeDelta > 0) {
				lastChange.Length += e.InsertionLength;
			} else {
				lastChange.Length -= e.RemovalLength;
			}
		}
	}

	class ChangeInfo
	{
		int offset;

		public ChangeInfo (int off, SeekableTextReader buffer)
		{
			offset = off;
			Length = 0;
			Buffer = buffer;
		}

		public int StartOffset {
			get	{ return offset; }
			private set { }
		}

		public int Length { get; set; }
		public int AbsoluteLength {
			get { return Math.Abs (Length); }
			private set { }
		}

		public SeekableTextReader Buffer { get; set; }
		public bool DeleteChange { get { return Length < 0; } }
	}
}
