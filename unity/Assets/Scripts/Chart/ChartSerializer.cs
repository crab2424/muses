using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Muses.Chart
{
    /// <summary>editor-spec.md §1.6。&lt;difficulty&gt;.muses のヘッダ部（@SONG/@DIFFICULTY/@LEVEL/@CHARTER）。</summary>
    public struct ChartFileHeader
    {
        public string songFile;
        public string difficulty;
        public int level;
        public string charter;
    }

    /// <summary>
    /// editor-spec.md §1。song.muses / &lt;difficulty&gt;.muses（行指向の独自テキスト形式）の読み書き。
    /// UnityEngine 非依存の純粋C#（ゲーム本体もこれで譜面を読むためEditor専用にしない）。
    /// 書き出しは常に列を桁揃えせず単純な区切りで出す（パーサは空白量に寛容なため読み込みには支障ない。
    /// 桁揃えの整形はエディタUI側の保存処理が別途担当する想定）。
    /// </summary>
    public static class ChartSerializer
    {
        /// <summary>editor-ui-rework-r9.md §4: 曲メタと譜面で拡張子を分ける。曲メタのファイル名は
        /// 常にこれで固定（フォルダ内に1つだけ存在し、§3の「曲プロジェクトかどうか」の判定にも使う）。</summary>
        public const string SongFileName = "song.museproj";
        public const string SongExt = ".museproj";
        public const string ChartExt = ".muses";

        /// <summary>r9以前に書かれた曲メタの旧ファイル名。読み込み時のフォールバックにのみ使う
        /// （書き出しは常にSongFileName、自動リネームはしない）。</summary>
        public const string LegacySongFileName = "song.muses";

        // ---------- song.museproj ----------

        public static SongMeta ReadSongMeta(string path)
        {
            var meta = new SongMeta();
            var lines = File.ReadAllLines(path);
            string section = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith("@"))
                {
                    var (key, value) = SplitHeader(trimmed);
                    switch (key)
                    {
                        case "FORMAT": break;
                        case "TITLE": meta.title = value; break;
                        case "ARTIST": meta.artist = value; break;
                        case "AUDIO": meta.audio = value; break;
                        case "JACKET": meta.jacket = value; break;
                        case "PREVIEW":
                        {
                            var pv = SplitWhitespace(value);
                            meta.previewStart = ParseFloat(pv[0]);
                            meta.previewEnd = ParseFloat(pv[1]);
                            break;
                        }
                        case "OFFSET": meta.offsetSec = ParseFloat(value); break;
                        default: throw new FormatException($"{path}:{i + 1}: unknown header '@{key}'");
                    }
                    continue;
                }

                if (trimmed.StartsWith("["))
                {
                    section = trimmed.Trim('[', ']').Trim();
                    continue;
                }

                string body = StripComment(trimmed);
                if (body.Length == 0) continue;
                var cols = SplitWhitespace(body);

                switch (section)
                {
                    case "METER":
                    {
                        int bar = int.Parse(cols[0], CultureInfo.InvariantCulture);
                        var nd = cols[1].Split('/');
                        meta.meters.Add(new MeterEvent
                        {
                            bar = bar,
                            numerator = int.Parse(nd[0], CultureInfo.InvariantCulture),
                            denominator = int.Parse(nd[1], CultureInfo.InvariantCulture),
                        });
                        break;
                    }
                    case "BPM":
                    {
                        var addr = SongAddr.ParseAddr(cols[0]);
                        int tick = SongAddr.ToTick(meta.meters, addr.bar, addr.beat, addr.tick);
                        meta.bpmEvents.Add(new BpmEvent { tick = tick, bpm = ParseFloat(cols[1]) });
                        break;
                    }
                    default:
                        throw new FormatException($"{path}:{i + 1}: body line outside a known section");
                }
            }

            meta.meters.Sort((a, b) => a.bar.CompareTo(b.bar));
            meta.bpmEvents.Sort((a, b) => a.tick.CompareTo(b.tick));
            return meta;
        }

        public static void WriteSongMeta(SongMeta meta, string path)
        {
            var sb = new StringBuilder();
            sb.Append("@FORMAT   muses-song 1\n");
            sb.Append($"@TITLE    {meta.title}\n");
            sb.Append($"@ARTIST   {meta.artist}\n");
            sb.Append($"@AUDIO    {meta.audio}\n");
            sb.Append($"@JACKET   {meta.jacket}\n");
            sb.Append($"@PREVIEW  {F(meta.previewStart)} {F(meta.previewEnd)}\n");
            sb.Append($"@OFFSET   {F(meta.offsetSec)}\n");

            sb.Append("\n[METER]\n");
            var meters = new List<MeterEvent>(meta.meters);
            meters.Sort((a, b) => a.bar.CompareTo(b.bar));
            foreach (var m in meters)
                sb.Append($"  {m.bar}  {m.numerator}/{m.denominator}\n");

            sb.Append("\n[BPM]\n");
            var bpms = new List<BpmEvent>(meta.bpmEvents);
            bpms.Sort((a, b) => a.tick.CompareTo(b.tick));
            foreach (var e in bpms)
            {
                var addr = SongAddr.ToAddr(meta.meters, e.tick);
                sb.Append($"  {SongAddr.FormatAddr(addr)}  {F(e.bpm)}\n");
            }

            WriteAllTextLf(path, sb.ToString());
        }

        // ---------- <difficulty>.muses ----------

        /// <summary>
        /// 譜面ファイルを読む。addr変換には song.meters を、tick→秒変換には song.bpmEvents を使う
        /// （editor-spec.md §1.5「曲メタと譜面を分離」どおり、BPMは常にsong側の値を採用する。
        /// chart.bpmEvents はこの呼び出しの中で song.bpmEvents のコピーで埋められる）。
        /// </summary>
        public static (ChartFileHeader header, ChartData chart) ReadChart(string path, SongMeta song)
        {
            var header = new ChartFileHeader();
            var chart = new ChartData();
            string section = null;
            Note pendingSlide = null;

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith("@"))
                {
                    var (key, value) = SplitHeader(trimmed);
                    switch (key)
                    {
                        case "FORMAT": break;
                        case "SONG": header.songFile = value; break;
                        case "DIFFICULTY": header.difficulty = value; break;
                        case "LEVEL": header.level = int.Parse(value, CultureInfo.InvariantCulture); break;
                        case "CHARTER": header.charter = value; break;
                        default: throw new FormatException($"{path}:{i + 1}: unknown header '@{key}'");
                    }
                    continue;
                }

                if (trimmed.StartsWith("["))
                {
                    if (pendingSlide != null) { chart.notes.Add(pendingSlide); pendingSlide = null; }
                    section = trimmed.Trim('[', ']').Trim();
                    continue;
                }

                string body = StripComment(trimmed);
                if (body.Length == 0) continue;
                bool isContinuation = body.StartsWith(">");
                if (isContinuation) body = body.Substring(1).Trim();
                var cols = SplitWhitespace(body);

                switch (section)
                {
                    case "SCROLL":
                    {
                        var addr = SongAddr.ParseAddr(cols[0]);
                        int tick = SongAddr.ToTick(song.meters, addr.bar, addr.beat, addr.tick);
                        var opts = ParseOptions(cols, 3);
                        var ev = new ScrollEvent
                        {
                            tick = tick,
                            group = int.Parse(cols[1], CultureInfo.InvariantCulture),
                            mul = ParseFloat(cols[2]),
                            easing = Easing.Linear,
                            durationTicks = 0,
                        };
                        if (opts.TryGetValue("dur", out var durS)) ev.durationTicks = int.Parse(durS, CultureInfo.InvariantCulture);
                        if (opts.TryGetValue("ease", out var easeS)) ev.easing = ParseEasing(easeS);
                        chart.scrollEvents.Add(ev);
                        break;
                    }
                    case "NOTES":
                    {
                        if (!isContinuation)
                        {
                            if (pendingSlide != null) { chart.notes.Add(pendingSlide); pendingSlide = null; }

                            var addr = SongAddr.ParseAddr(cols[0]);
                            int tick = SongAddr.ToTick(song.meters, addr.bar, addr.beat, addr.tick);
                            var kind = ParseKind(cols[1]);
                            float layerF = ParseLayer(cols[2]);
                            float cellF = ParseFloat(cols[3]);
                            float width = ParseFloat(cols[4]);
                            var opts = ParseOptions(cols, 5);

                            var note = new Note
                            {
                                kind = kind,
                                points = new List<Waypoint> { MakeWaypoint(tick, layerF, cellF, width, opts) },
                            };
                            if (opts.TryGetValue("grp", out var grpS))
                                note.scrollGroup = int.Parse(grpS, CultureInfo.InvariantCulture);

                            if (kind == NoteKind.Slide) pendingSlide = note;
                            else chart.notes.Add(note);
                        }
                        else
                        {
                            if (pendingSlide == null)
                                throw new FormatException($"{path}:{i + 1}: continuation line ('>') without a preceding slide note");

                            var addr = SongAddr.ParseAddr(cols[0]);
                            int tick = SongAddr.ToTick(song.meters, addr.bar, addr.beat, addr.tick);
                            float layerF = ParseLayer(cols[1]);
                            float cellF = ParseFloat(cols[2]);
                            float width = ParseFloat(cols[3]);
                            var opts = ParseOptions(cols, 4);
                            pendingSlide.points.Add(MakeWaypoint(tick, layerF, cellF, width, opts));
                        }
                        break;
                    }
                    default:
                        throw new FormatException($"{path}:{i + 1}: body line outside a known section");
                }
            }
            if (pendingSlide != null) chart.notes.Add(pendingSlide);

            chart.notes.Sort((a, b) => a.points[0].tick.CompareTo(b.points[0].tick));
            chart.bpmEvents = new List<BpmEvent>(song.bpmEvents);
            ChartFormat.ResolveTimes(chart);
            ChartFormat.ResolveSlideComboPoints(chart);
            return (header, chart);
        }

        /// <summary>addr変換には song.meters を使う（chart.bpmEventsはsong側の値なのでここでは書かない）。</summary>
        public static void WriteChart(string path, ChartFileHeader header, ChartData chart, SongMeta song)
        {
            var sb = new StringBuilder();
            sb.Append("@FORMAT     muses-chart 1\n");
            sb.Append($"@SONG       {header.songFile}\n");
            sb.Append($"@DIFFICULTY {header.difficulty}\n");
            sb.Append($"@LEVEL      {header.level}\n");
            sb.Append($"@CHARTER    {header.charter}\n");

            sb.Append("\n[SCROLL]\n");
            var scrolls = new List<ScrollEvent>(chart.scrollEvents);
            scrolls.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.group.CompareTo(b.group));
            foreach (var ev in scrolls)
            {
                var addr = SongAddr.ToAddr(song.meters, ev.tick);
                var opts = new List<string>();
                if (ev.durationTicks != 0) opts.Add($"dur={ev.durationTicks}");
                if (ev.easing != Easing.Linear) opts.Add($"ease={EasingToStr(ev.easing)}");

                sb.Append($"  {SongAddr.FormatAddr(addr)}  {ev.group}  {F(ev.mul)}");
                if (opts.Count > 0) sb.Append("  " + string.Join(" ", opts));
                sb.Append('\n');
            }

            sb.Append("\n[NOTES]\n");
            // editor-spec.md §1.3: 書き出し順は addr → layer → cell の昇順に正規化する（Slideは始点基準）。
            var notes = new List<Note>(chart.notes);
            notes.Sort((a, b) =>
            {
                var pa = a.points[0];
                var pb = b.points[0];
                int c = pa.tick.CompareTo(pb.tick);
                if (c != 0) return c;
                c = pa.layerF.CompareTo(pb.layerF);
                if (c != 0) return c;
                return pa.cellF.CompareTo(pb.cellF);
            });

            foreach (var note in notes)
            {
                var start = note.points[0];
                var addr = SongAddr.ToAddr(song.meters, start.tick);
                var opts = new List<string>();
                if (note.scrollGroup != 0) opts.Add($"grp={note.scrollGroup}");
                AppendWaypointOptions(opts, start);

                sb.Append($"  {SongAddr.FormatAddr(addr)}  {KindToStr(note.kind)}  {LayerToStr(start.layerF)}  {F(start.cellF)}  {F(start.width)}");
                if (opts.Count > 0) sb.Append("  " + string.Join(" ", opts));
                sb.Append('\n');

                for (int i = 1; i < note.points.Count; i++)
                {
                    var wp = note.points[i];
                    var wAddr = SongAddr.ToAddr(song.meters, wp.tick);
                    var wOpts = new List<string>();
                    AppendWaypointOptions(wOpts, wp);

                    sb.Append($"    >  {SongAddr.FormatAddr(wAddr)}  {LayerToStr(wp.layerF)}  {F(wp.cellF)}  {F(wp.width)}");
                    if (wOpts.Count > 0) sb.Append("  " + string.Join(" ", wOpts));
                    sb.Append('\n');
                }
            }

            WriteAllTextLf(path, sb.ToString());
        }

        /// <summary>editor-ui-rework-r2.md §6: easeは横(cellF/width)専用として据え置き、
        /// 高さ(layerF)用にeasehを新設する。easehはeasingと異なる場合のみ出力する
        /// （同値ならeaseh省略=既存フォーマットと同じ行になり、diffが最小になる）。</summary>
        private static void AppendWaypointOptions(List<string> opts, Waypoint wp)
        {
            if (wp.easing != Easing.Linear) opts.Add($"ease={EasingToStr(wp.easing)}");
            if (wp.easingH != wp.easing) opts.Add($"easeh={EasingToStr(wp.easingH)}");
            if (wp.marker != WaypointMarker.None) opts.Add($"mark={MarkerToStr(wp.marker)}");
            if (wp.comboStep.HasValue) opts.Add($"combo={wp.comboStep.Value}");
        }

        /// <summary>easeh省略時はeaseを両軸に流用する（既存譜面はease=1個のままで横高さとも
        /// 同じeasingがかかる、editor-ui-rework-r2.md §6の互換規則）。</summary>
        private static Waypoint MakeWaypoint(int tick, float layerF, float cellF, float width, Dictionary<string, string> opts)
        {
            var wp = new Waypoint
            {
                tick = tick,
                layerF = layerF,
                cellF = cellF,
                width = width,
                easing = Easing.Linear,
                easingH = Easing.Linear,
                marker = WaypointMarker.None,
                comboStep = null,
            };
            if (opts.TryGetValue("ease", out var e)) wp.easing = wp.easingH = ParseEasing(e);
            if (opts.TryGetValue("easeh", out var eh)) wp.easingH = ParseEasing(eh);
            if (opts.TryGetValue("mark", out var m)) wp.marker = ParseMarker(m);
            if (opts.TryGetValue("combo", out var c)) wp.comboStep = int.Parse(c, CultureInfo.InvariantCulture);
            return wp;
        }

        // ---------- 字句解析ヘルパー ----------

        /// <summary>
        /// "@KEY 値" を分解する。値は行末までだが、末尾コメント（" # ..."）は他の行と同じ規則で取り除く。
        /// タイトル等の自由記述に # を含めたい場合との衝突は起こりうるが、editor-spec.md の例
        /// （@PREVIEW/@OFFSET に行末コメントを付けている）に合わせてこの規則を優先する。
        /// </summary>
        private static (string key, string value) SplitHeader(string trimmedLine)
        {
            int sp = IndexOfWhitespace(trimmedLine);
            string key = sp < 0 ? trimmedLine.Substring(1) : trimmedLine.Substring(1, sp - 1);
            string value = sp < 0 ? "" : StripComment(trimmedLine.Substring(sp + 1));
            return (key, value);
        }

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return i;
            return -1;
        }

        private static string StripComment(string s)
        {
            int idx = s.IndexOf('#');
            return (idx >= 0 ? s.Substring(0, idx) : s).Trim();
        }

        private static string[] SplitWhitespace(string s) =>
            s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        private static Dictionary<string, string> ParseOptions(string[] cols, int startIdx)
        {
            var d = new Dictionary<string, string>();
            for (int i = startIdx; i < cols.Length; i++)
            {
                int eq = cols[i].IndexOf('=');
                if (eq < 0) throw new FormatException($"invalid option token '{cols[i]}' (expected key=value)");
                d[cols[i].Substring(0, eq)] = cols[i].Substring(eq + 1);
            }
            return d;
        }

        private static float ParseFloat(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static string F(float v) => v.ToString("0.###############", CultureInfo.InvariantCulture);

        private static void WriteAllTextLf(string path, string content)
        {
            content = content.Replace("\r\n", "\n");
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        // ---------- 列挙型 ↔ 文字列 ----------

        private static string KindToStr(NoteKind k) => k switch
        {
            NoteKind.Tap => "tap",
            NoteKind.ExTap => "extap",
            NoteKind.Slide => "slide",
            NoteKind.Flick => "flick",
            _ => throw new ArgumentOutOfRangeException(nameof(k)),
        };

        private static NoteKind ParseKind(string s) => s.ToLowerInvariant() switch
        {
            "tap" => NoteKind.Tap,
            "extap" => NoteKind.ExTap,
            "slide" => NoteKind.Slide,
            "flick" => NoteKind.Flick,
            _ => throw new FormatException($"unknown note kind '{s}'"),
        };

        private static string EasingToStr(Easing e) => e switch
        {
            Easing.Linear => "linear",
            Easing.Smooth => "smooth",
            Easing.SineIn => "sinein",
            Easing.SineOut => "sineout",
            Easing.SineInOut => "sineinout",
            Easing.QuadIn => "quadin",
            Easing.QuadOut => "quadout",
            _ => throw new ArgumentOutOfRangeException(nameof(e)),
        };

        private static Easing ParseEasing(string s) => s.ToLowerInvariant() switch
        {
            "linear" => Easing.Linear,
            "smooth" => Easing.Smooth,
            "sinein" => Easing.SineIn,
            "sineout" => Easing.SineOut,
            "sineinout" => Easing.SineInOut,
            "quadin" => Easing.QuadIn,
            "quadout" => Easing.QuadOut,
            _ => throw new FormatException($"unknown easing '{s}'"),
        };

        private static string MarkerToStr(WaypointMarker m) => m switch
        {
            WaypointMarker.Visible => "vis",
            WaypointMarker.Invisible => "invis",
            _ => throw new ArgumentOutOfRangeException(nameof(m)),
        };

        private static WaypointMarker ParseMarker(string s) => s.ToLowerInvariant() switch
        {
            "vis" => WaypointMarker.Visible,
            "invis" => WaypointMarker.Invisible,
            _ => throw new FormatException($"unknown marker '{s}'"),
        };

        private static string LayerToStr(float layerF)
        {
            if (layerF == 0f) return "G";
            if (layerF == 1f) return "S";
            return F(layerF);
        }

        private static float ParseLayer(string s)
        {
            if (s == "G") return 0f;
            if (s == "S") return 1f;
            return ParseFloat(s);
        }
    }
}
