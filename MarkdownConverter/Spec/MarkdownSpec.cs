﻿using FSharp.Markdown;
using MarkdownConverter.Grammar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarkdownConverter.Spec
{
    class MarkdownSpec
    {
        private string text;
        private IEnumerable<string> files;
        public EbnfGrammar Grammar { get; set; } = new EbnfGrammar();
        public List<SectionRef> Sections { get; set; } = new List<SectionRef>();
        public List<ProductionRef> Productions { get; set; } = new List<ProductionRef>();
        public Reporter Report { get; set; } = new Reporter();
        public IEnumerable<Tuple<string, MarkdownDocument>> Sources { get; set; }

        public static MarkdownSpec ReadString(string s)
        {
            var md = new MarkdownSpec { text = s };
            md.Init();
            return md;
        }

        public static MarkdownSpec ReadFiles(IEnumerable<string> files, List<Tuple<int, string, string, SourceLocation>> readme_headings)
        {
            var md = new MarkdownSpec { files = files };
            md.Init();

            var md_headings = md.Sections.Where(s => s.Level <= 2).ToList();
            if (readme_headings != null && md_headings.Count > 0)
            {
                var readme_order = (from readme in readme_headings
                                    select new
                                    {
                                        orderInBody = md_headings.FindIndex(mdh => readme.Item1 == mdh.Level && readme.Item3 == mdh.Url),
                                        level = readme.Item1,
                                        title = readme.Item2,
                                        url = readme.Item3,
                                        loc = readme.Item4
                                    }).ToList();

                // The readme order should go "1,2,3,..." up to md_headings.Last()
                int expected = 0;
                foreach (var readme in readme_order)
                {
                    if (readme.orderInBody == -1)
                    {
                        var link = $"{new string(' ', readme.level * 2 - 2)}* [{readme.title}]({readme.url})";
                        md.Report.Error("MD25", $"Remove: {link}", readme.loc);
                    }
                    else if (readme.orderInBody < expected)
                    {
                        continue; // error has already been reported
                    }
                    else if (readme.orderInBody == expected)
                    {
                        expected++; continue;
                    }
                    else if (readme.orderInBody > expected)
                    {
                        for (int missing = expected; missing < readme.orderInBody; missing++)
                        {
                            var s = md_headings[missing];
                            var link = $"{new string(' ', s.Level * 2 - 2)}* [{s.Title}]({s.Url})";
                            md.Report.Error("MD24", $"Insert: {link}", readme.loc);
                        }
                        expected = readme.orderInBody + 1;
                    }
                }
            }

            return md;
        }

        private void Init()
        {
            // (0) Read all the markdown docs.
            // We do so in a parallel way, being careful not to block any threadpool threads on IO work;
            // only on CPU work.
            if (text != null)
            {
                Sources = new[] { Tuple.Create("", Markdown.Parse(BugWorkaroundEncode(text))) };
            }
            if (files != null)
            {
                var tasks = new List<Task<Tuple<string, MarkdownDocument>>>();
                foreach (var fn in files)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        using (var reader = File.OpenText(fn))
                        {
                            var s = await reader.ReadToEndAsync();
                            s = BugWorkaroundEncode(s);
                            return Tuple.Create(fn, Markdown.Parse(s));
                        }
                    }));
                }
                Sources = Task.WhenAll(tasks).GetAwaiter().GetResult();
            }


            // (1) Add sections into the dictionary
            int h1 = 0, h2 = 0, h3 = 0, h4 = 0;
            string url = "", title = "";

            // (2) Turn all the antlr code blocks into a grammar
            var sbantlr = new StringBuilder();

            foreach (var src in Sources)
            {
                Report.CurrentFile = Path.GetFullPath(src.Item1);
                var filename = Path.GetFileName(src.Item1);
                var md = src.Item2;

                foreach (var mdp in md.Paragraphs)
                {
                    Report.CurrentParagraph = mdp;
                    Report.CurrentSection = null;
                    if (mdp.IsHeading)
                    {
                        try
                        {
                            var sr = new SectionRef(mdp as MarkdownParagraph.Heading, filename);
                            if (sr.Level == 1) { h1 += 1; h2 = 0; h3 = 0; h4 = 0; sr.Number = $"{h1}"; }
                            if (sr.Level == 2) { h2 += 1; h3 = 0; h4 = 0; sr.Number = $"{h1}.{h2}"; }
                            if (sr.Level == 3) { h3 += 1; h4 = 0; sr.Number = $"{h1}.{h2}.{h3}"; }
                            if (sr.Level == 4) { h4 += 1; sr.Number = $"{h1}.{h2}.{h3}.{h4}"; }
                            //
                            if (sr.Level > 4)
                            {
                                Report.Error("MD01", "Only support heading depths up to ####");
                            }
                            else if (Sections.Any(s => s.Url == sr.Url))
                            {
                                Report.Error("MD02", $"Duplicate section title {sr.Url}");
                            }
                            else
                            {
                                Sections.Add(sr);
                                url = sr.Url;
                                title = sr.Title;
                                Report.CurrentSection = sr;
                            }
                        }
                        catch (Exception ex)
                        {
                            Report.Error("MD03", ex.Message); // constructor of SectionRef might throw
                        }
                    }
                    else if (mdp.IsCodeBlock)
                    {
                        var mdc = mdp as MarkdownParagraph.CodeBlock;
                        string code = mdc.code, lang = mdc.language;
                        if (lang != "antlr") continue;
                        var g = Antlr.ReadString(code, "");
                        Productions.Add(new ProductionRef(code, g.Productions));
                        foreach (var p in g.Productions)
                        {
                            p.Link = url; p.LinkName = title;
                            if (p.ProductionName != null && Grammar.Productions.Any(dupe => dupe.ProductionName == p.ProductionName))
                            {
                                Report.Warning("MD04", $"Duplicate grammar for {p.ProductionName}");
                            }
                            Grammar.Productions.Add(p);
                        }
                    }
                }



            }
        }


        private static string BugWorkaroundEncode(string src)
        {
            var lines = src.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // https://github.com/tpetricek/FSharp.formatting/issues/388
            // The markdown parser doesn't recognize | inside inlinecode inside table
            // To work around that, we'll encode this |, then decode it later
            for (int li = 0; li < lines.Length; li++)
            {
                if (!lines[li].StartsWith("|")) continue;
                var codes = lines[li].Split('`');
                for (int ci = 1; ci < codes.Length; ci += 2)
                {
                    codes[ci] = codes[ci].Replace("|", "ceci_n'est_pas_une_pipe");
                }
                lines[li] = string.Join("`", codes);
            }

            // https://github.com/tpetricek/FSharp.formatting/issues/347
            // The markdown parser overly indents a level1 paragraph if it follows a level2 bullet
            // To work around that, we'll call out now, then unindent it later
            var state = 0; // 1=found.level1, 2=found.level2
            for (int li = 0; li < lines.Length - 1; li++)
            {
                if (lines[li].StartsWith("*  "))
                {
                    state = 1;
                    if (string.IsNullOrWhiteSpace(lines[li + 1])) li++;
                }
                else if ((state == 1 || state == 2) && lines[li].StartsWith("   * "))
                {
                    state = 2;
                    if (string.IsNullOrWhiteSpace(lines[li + 1])) li++;
                }
                else if (state == 2 && lines[li].StartsWith("      ") && lines[li].Length > 6 && lines[li][6] != ' ')
                {
                    state = 2;
                    if (string.IsNullOrWhiteSpace(lines[li + 1])) li++;
                }
                else if (state == 2 && lines[li].StartsWith("   ") && lines[li].Length > 3 && lines[li][3] != ' ')
                {
                    lines[li] = "   ceci-n'est-pas-une-indent" + lines[li].Substring(3);
                    state = 0;
                }
                else
                {
                    state = 0;
                }
            }

            src = string.Join("\r\n", lines);

            // https://github.com/tpetricek/FSharp.formatting/issues/390
            // The markdown parser doesn't recognize bullet-chars inside codeblocks inside lists
            // To work around that, we'll prepend the line with stuff, and remove it later
            var codeblocks = src.Split(new[] { "\r\n    ```" }, StringSplitOptions.None);
            for (int cbi = 1; cbi < codeblocks.Length; cbi += 2)
            {
                var s = codeblocks[cbi];
                s = s.Replace("\r\n    *", "\r\n    ceci_n'est_pas_une_*");
                s = s.Replace("\r\n    +", "\r\n    ceci_n'est_pas_une_+");
                s = s.Replace("\r\n    -", "\r\n    ceci_n'est_pas_une_-");
                codeblocks[cbi] = s;
            }

            return string.Join("\r\n    ```", codeblocks);
        }

    }
}