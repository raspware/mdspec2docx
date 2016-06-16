﻿// TODO:
//
// * Something goofy is going on with the TOC in the Word document. The field codes are broken, so it
//   isn't recognized as a field. And if I edit the field in Word (e.g. to add page numbers) then all
//   within-spec section links get broken.

using Grammar2Html;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

static class Program
{

    static int Main(string[] args)
    {
        // mdspec2docx *.md csharp.g4 template.docx -o readme.doc -o grammar.html -interactive
        var ifiles = new List<string>();
        var ofiles = new List<string>();
        bool isinteractive = false;
        string argserror = "";
        for (int i=0; i<args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("-"))
            {
                if (arg == "-o" && i < args.Length - 1) { i++; ofiles.Add(args[i]); }
                else if (arg == "-interactive") { isinteractive = true; }
                else argserror += $"Unrecognized '{arg}'\n";
            }
            else if (!arg.Contains("*") && !arg.Contains("?"))
            {
                if (!File.Exists(arg)) { Console.Error.WriteLine($"Not found - {arg}"); return 1; }
                ifiles.Add(arg);
            }
            else
            {
                // Windows command-shell doesn't do globbing, so we have to do it ourselves
                string dir = Path.GetDirectoryName(arg), filename = Path.GetFileName(arg);
                if (dir.Contains("*") || dir.Contains("?")) { Console.Error.WriteLine("Can't match wildcard directory names"); return 1; }
                if (dir == "") dir = Directory.GetCurrentDirectory();
                if (!Directory.Exists(dir)) { Console.Error.WriteLine($"Not found - \"{dir}\""); return 1; }
                var fns2 = Directory.GetFiles(dir, filename);
                if (fns2.Length == 0) { Console.Error.WriteLine($"Not found - \"{arg}\""); return 1; }
                ifiles.AddRange(fns2);
            }
        }

        var imdfiles = new List<string>();
        string ireadmefile = null, iantlrfile = null, idocxfile = null, ohtmlfile = null, odocfile = null;
        foreach (var ifile in ifiles)
        {
            var name = Path.GetFileName(ifile);
            var ext = Path.GetExtension(ifile).ToLower();
            if (ext == ".g4") { if (iantlrfile != null) argserror += "Multiple input .g4 files\n"; iantlrfile = ifile; }
            else if (ext == ".docx") { if (idocxfile != null) argserror += "Multiple input .docx files\n"; idocxfile = ifile; }
            else if (ext != ".md") { argserror += $"Not .g4 or .docx or .md '{ifile}'\n";  continue; }
            else if (String.Equals(name,"readme.md",StringComparison.InvariantCultureIgnoreCase)) { if (ireadmefile != null) argserror += "Multiple readme.md files\n";  ireadmefile = ifile; }
            else { imdfiles.Add(ifile); }
        }
        foreach (var ofile in ofiles)
        {
            var ext = Path.GetExtension(ofile).ToLower();
            if (ext == ".html" || ext == ".htm") { if (ohtmlfile != null) argserror += "Multiple output html files\n"; ohtmlfile = ofile; }
            if (ext == ".docx") { if (odocfile != null) argserror += "Multiple output docx files\n"; odocfile = ofile; }
        }

        if (ohtmlfile != null && iantlrfile == null) argserror += "Can't output .html without having .g4 input\n";
        if (odocfile == null) argserror += "No output .docx file specified\n";
        if (ireadmefile == null && ifiles.Count == 0) argserror += "No .md files supplied\n";
        if (idocxfile == null) argserror += "No template.docx supplied\n";

        if (argserror != "")
        {
            Console.Error.WriteLine(argserror);
            Console.Error.WriteLine("mdspec2docx *.md grammar.g4 template.docx -o spec.docx -o grammar.html -interactive");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Turns the markdown files into a word document based on the template.");
            Console.Error.WriteLine("If readme.md and other files are given, then readme is used solely to");
            Console.Error.WriteLine("   sort the docx based on its list of `* [Link](subfile.md)`.");
            Console.Error.WriteLine("If a .g4 is given, it verifies 1:1 correspondence with ```antlr blocks.");
            Console.Error.WriteLine("The -interactive flag causes the html+docx to be opened once done.");
            return 1;
        }

        Console.WriteLine("mdspec2docx");
        Console.WriteLine("Reading markdown files");

        // Read input file. If it contains a load of linked filenames, then read them instead.
        List<string> ifiles_in_order = new List<string>();
        if (ireadmefile == null)
        {
            ifiles_in_order.AddRange(ifiles);
        }
        else if (ireadmefile != null && ifiles.Count == 0)
        {
            ifiles_in_order.Add(ireadmefile);
        }
        else
        {
            var readme = FSharp.Markdown.Markdown.Parse(File.ReadAllText(ireadmefile));
            var links =
                (from list in readme.Paragraphs.OfType<FSharp.Markdown.MarkdownParagraph.ListBlock>()
                 let items = list.Item2
                 from par in items
                 from spanpar in par.OfType<FSharp.Markdown.MarkdownParagraph.Span>()
                 let spans = spanpar.Item
                 from link in spans.OfType<FSharp.Markdown.MarkdownSpan.DirectLink>()
                 let url = link.Item2.Item1
                 where url.EndsWith(".md", StringComparison.InvariantCultureIgnoreCase)
                 select url).ToList().Distinct();
            foreach (var link in links)
            {
                var ifile = ifiles.FirstOrDefault(f => Path.GetFileName(f) == link);
                if (ifile == null) { Console.Error.WriteLine($"readme.md link '{link}' wasn't one of the files passed on the command line"); return 1; }
                ifiles_in_order.Add(ifile);
            }
        }
        var md = MarkdownSpec.ReadFiles(ifiles_in_order);


        // Now md.Gramar contains the grammar as extracted out of the *.md files, and moreover has
        // correct references to within the spec. We'll check that it has the same productions as
        // in the corresponding ANTLR file
        if (iantlrfile != null)
        {
            Console.WriteLine($"Reading {Path.GetFileName(iantlrfile)}");
            var grammar = Antlr.ReadFile(iantlrfile);
            foreach (var diff in CompareGrammars(grammar, md.Grammar))
            {
                if (diff.authority == null) Report("MD21","error",$"markdown has superfluous production '{diff.productionName}'","mdspec2docx");
                else if (diff.copy == null) Report("MD22","error",$"markdown lacks production '{diff.productionName}'","mdspec2docx");
                else
                {
                    Report("MD23","error",$"production '{diff.productionName}' differs between markdown and antlr.g4","mdspec2docx");
                    Report("MD23b","error","antlr.g4 says " + diff.authority.Replace("\r", "\\r").Replace("\n", "\\n"),"mdspec2docx");
                    Report("MD23c","error","markdown says " + diff.copy.Replace("\r", "\\r").Replace("\n", "\\n"),"mdspec2docx");
                }
            }
            foreach (var p in grammar.Productions)
            {
                p.Link = md.Grammar.Productions.FirstOrDefault(mdp => mdp?.ProductionName == p.ProductionName)?.Link;
                p.LinkName = md.Grammar.Productions.FirstOrDefault(mdp => mdp?.ProductionName == p.ProductionName)?.LinkName;
            }

            if (ohtmlfile != null) File.WriteAllText(ohtmlfile, grammar.ToHtml(), Encoding.UTF8);
            if (ohtmlfile != null && isinteractive) Process.Start(ohtmlfile);
        }

        // Generate the Specification.docx file
        if (odocfile != null)
        {
            if (isinteractive) odocfile = PickUniqueFilename(odocfile);
            Console.WriteLine($"Writing {Path.GetFileName(odocfile)}");
            md.WriteFile(idocxfile, odocfile);
            if (isinteractive) Process.Start(odocfile);
        }
        return 0;
    }

    static int ireport = 0;
    public static void Report(string code, string severity, string msg, string loc)
    {
        ireport++;
        Console.Error.WriteLine($"{loc}: {severity} {code}: {ireport:D3}. {msg}");
    }


    static string PickUniqueFilename(string suggestion)
    {
        var dir = Path.GetFileNameWithoutExtension(suggestion);
        var ext = Path.GetExtension(suggestion);

        for (int ifn=0; true; ifn++)
        { 
            var fn = dir + (ifn == 0 ? "" : ifn.ToString()) + ext;
            if (!File.Exists(fn)) return fn;
            try { File.Delete(fn); return fn; } catch (Exception) { }
            ifn++;
        }
    }

    class ProductionDifference
    {
        public string productionName;
        public string authority, copy;
    }

    static IEnumerable<ProductionDifference> CompareGrammars(Grammar authority, Grammar copy)
    {
        Func<Grammar, Dictionary<string, Production>> ToDictionary;
        ToDictionary = g =>
        {
            var d = new Dictionary<string, Production>();
            foreach (var pp in g.Productions) if (pp.ProductionName != null) d[pp.ProductionName] = pp;
            return d;
        };
        var dauthority = ToDictionary(authority);
        var dcopy = ToDictionary(copy);

        foreach (var p in dauthority.Keys)
        {
            if (!dcopy.ContainsKey(p)) continue;
            Production pauthority0 = dauthority[p], pcopy0 = dcopy[p];
            string pauthority = Antlr.ToString(pauthority0), pcopy = Antlr.ToString(pcopy0);
            if (pauthority == pcopy) continue;
            yield return new ProductionDifference { productionName = p, authority = pauthority, copy = pcopy };
        }

        foreach (var p in dauthority.Keys)
        {
            if (p == "start") continue;
            if (!dcopy.ContainsKey(p)) yield return new ProductionDifference { productionName = p, authority = "<defined>", copy = null };
        }
        foreach (var p in dcopy.Keys)
        {
            if (p == "start") continue;
            if (!dauthority.ContainsKey(p)) yield return new ProductionDifference { productionName = p, authority = null, copy = "<defined>" };
        }
    }


    public static T Option<T>(this FSharpOption<T> o) where T : class
    {
        if (FSharpOption<T>.GetTag(o) == FSharpOption<T>.Tags.None) return null;
        return o.Value;
    }

}