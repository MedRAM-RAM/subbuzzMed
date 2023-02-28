﻿using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using subbuzz.Extensions;
using subbuzz.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

#if EMBY
using MediaBrowser.Common.Net;
using ILogger = MediaBrowser.Model.Logging.ILogger;
#else
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger<subbuzz.Providers.SubBuzz>;
#endif

#if JELLYFIN
using System.Net.Http;
#endif

namespace subbuzz.Providers
{
    class SubsUnacsNet : ISubBuzzProvider, IHasOrder
    {
        internal const string NAME = "subsunacs.net";
        private const string ServerUrl = "https://subsunacs.net";
        private const string HttpReferer = "https://subsunacs.net/search.php";
        private readonly List<string> Languages = new List<string> { "bg", "en" };

        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly ILocalizationManager _localizationManager;
        private readonly ILibraryManager _libraryManager;
        private Download downloader;

        private static Dictionary<string, string> InconsistentTvs = new Dictionary<string, string>
        {
            { "Marvel's Daredevil", "Daredevil" },
            { "Marvel's Luke Cage", "Luke Cage" },
            { "Marvel's Iron Fist", "Iron Fist" },
            { "DC's Legends of Tomorrow", "Legends of Tomorrow" },
            { "Doctor Who (2005)", "Doctor Who" },
            { "Star Trek: Deep Space Nine", "Star Trek DS9" },
            { "Star Trek: The Next Generation", "Star Trek TNG" },
            { "La Casa de Papel", "Money Heist" },
            { "Star Wars: Andor", "Andor" },
        };

        private static Dictionary<string, string> InconsistentMovies = new Dictionary<string, string>
        {
            { "Back to the Future Part III", "Back to the Future 3" },
            { "Back to the Future Part II", "Back to the Future 2" },
            { "Bill & Ted Face the Music", "Bill Ted Face the Music" },
            { "The Protégé", "The Protege"},
        };

        public string Name => $"[{Plugin.NAME}] <b>{NAME}</b>";

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new List<VideoContentType> { VideoContentType.Episode, VideoContentType.Movie };

        public int Order => 0;

        public SubsUnacsNet(
            ILogger logger,
            IFileSystem fileSystem,
            ILocalizationManager localizationManager,
            ILibraryManager libraryManager,
#if JELLYFIN
            IHttpClientFactory http
#else
            IHttpClient http
#endif
            )
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _localizationManager = localizationManager;
            _libraryManager = libraryManager;
            downloader = new Download(http);
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            try
            {
                return await downloader.GetArchiveSubFile(
                    id, 
                    HttpReferer, 
                    Encoding.GetEncoding(1251),
                    Plugin.Instance.Configuration.EncodeSubtitlesToUTF8,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{NAME}: GetSubtitles error: {e}");
            }

            return new SubtitleResponse();
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request,
            CancellationToken cancellationToken)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var res = new List<SubtitleInfo>();

            try
            {
                if (!Plugin.Instance.Configuration.EnableSubsunacsNet)
                {
                    // provider is disabled
                    return res;
                }

                SearchInfo si = SearchInfo.GetSearchInfo(
                    request,
                    _localizationManager,
                    _libraryManager,
                    "{0} {1:D2}x{2:D2}",
                    "{0} {1:D2} Season",
                    InconsistentTvs,
                    InconsistentMovies);

                _logger.LogInformation($"{NAME}: Request subtitle for '{si.SearchText}', language={si.Lang}, year={request.ProductionYear}");

                if (!Languages.Contains(si.Lang) || String.IsNullOrEmpty(si.SearchText))
                {
                    return res;
                }

                si.SearchText = si.SearchText.Replace(':', ' ').Replace("  ", " ");
                si.SearchEpByName = si.SearchEpByName.Replace(':', ' ').Replace("  ", " ");

                var tasks = new List<Task<List<SubtitleInfo>>>();

                if (!String.IsNullOrEmpty(si.SearchText))
                {
                    // search for movies/series by title
                    var post_params = GetPostParams(
                        si.SearchText,
                        si.Lang != "en" ? "0" : "1",
                        request.ContentType == VideoContentType.Movie ? Convert.ToString(request.ProductionYear) : "");

                    tasks.Add(SearchUrl($"{ServerUrl}/search.php", post_params, si, cancellationToken));
                }

                if (request.ContentType == VideoContentType.Episode && !String.IsNullOrEmpty(si.SearchEpByName) && (si.SeasonNumber ?? 0) == 0)
                {
                    // Search for special episodes by name
                    var post_params = GetPostParams(si.SearchEpByName, si.Lang != "en" ? "0" : "1", "");
                    tasks.Add(SearchUrl($"{ServerUrl}/search.php", post_params, si, cancellationToken));
                }

                if (request.ContentType == VideoContentType.Episode && !String.IsNullOrWhiteSpace(si.SearchSeason) && (si.SeasonNumber ?? 0) > 0)
                {
                    // search for episodes in season packs
                    var post_params = GetPostParams(si.SearchSeason, si.Lang != "en" ? "0" : "1", "");
                    tasks.Add(SearchUrl($"{ServerUrl}/search.php", post_params, si, cancellationToken));
                }

                foreach (var task in tasks)
                {
                    List<SubtitleInfo> subs = await task;
                    Utils.MergeSubtitleInfo(res, subs);
                }

                //res.Sort((x, y) => y.Score.CompareTo(x.Score));
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{NAME}: Search error: {e}");
            }

            watch.Stop();
            _logger.LogInformation($"{NAME}: Search duration: {watch.ElapsedMilliseconds / 1000.0} sec. Subtitles found: {res.Count}");

            return res;
        }

        protected Dictionary<string, string> GetPostParams(string m, string l, string y)
        {
            return new Dictionary<string, string>
                {
                    { "m", m }, // search text
                    { "l", l }, // language - 0: bulgarian, 1: english
                    { "c", "" }, // country
                    { "y", y }, // year
                    { "action", "   Търси   " },
                    { "a", "" }, // actor
                    { "d", "" }, // director
                    { "u", "" }, // uploader
                    { "g", "" }, // genre
                    { "t", "" },
                    { "imdbcheck", "1" }
                };
        }

        protected async Task<List<SubtitleInfo>> SearchUrl(string url, Dictionary<string, string> post_params, SearchInfo si, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation($"{NAME}: " + (post_params != null ? $"POST: {url} -> " + post_params["m"] : $"GET: {url}"));

                using (var html = await downloader.GetStream(url, HttpReferer, post_params, cancellationToken))
                {
                    return await ParseHtml(html, si, cancellationToken);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{NAME}: GET: {url}: Search error: {e}");
                return new List<SubtitleInfo>();
            }
        }

        protected async Task<List<SubtitleInfo>> ParseHtml(System.IO.Stream html, SearchInfo si, CancellationToken cancellationToken)
        {
            var res = new List<SubtitleInfo>();

            var config = AngleSharp.Configuration.Default;
            var context = AngleSharp.BrowsingContext.New(config);
            var parser = new AngleSharp.Html.Parser.HtmlParser(context);
            var htmlDoc = parser.ParseDocument(html);

            var trNodes = htmlDoc.QuerySelectorAll("tr[onmouseover]");
            foreach (var tr in trNodes)
            {
                var tdNodes = tr.GetElementsByTagName("td");
                if (tdNodes == null || tdNodes.Count() < 6) continue;

                var link = tdNodes[0].QuerySelector("a");
                if (link == null) continue;

                string subLink = ServerUrl + link.GetAttribute("href");
                string subTitle = link.InnerHtml;

                var year = link.NextElementSibling;
                string subYear = year != null ? year.InnerHtml.Replace("&nbsp;", " ") : "";
                subYear = subYear.Trim(new[] { ' ', '(', ')' });
                subTitle += $" ({subYear})";

                var subScoreBase = new SubtitleScore();
                si.MatchTitle(subTitle, ref subScoreBase);

                string subNotes = link.GetAttribute("title");
                string subDate = string.Empty;
                string subInfoBase = string.Empty; ;
                string subInfo = string.Empty; ;

                var regex = new Regex(@"(?:.*<b>Дата: </b>)(?<date>.*)(?:<br><b>Инфо: </b><br>)(?<notes>.*)");
                var regexImg = new Regex(@"<img[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var subNotesMatch = regex.Match(subNotes);

                if (subNotesMatch.Success)
                {
                    subDate = subNotesMatch.Groups["date"].Value;
                    subInfoBase = subNotesMatch.Groups["notes"].Value.Replace("</div>", string.Empty);
                    subInfoBase = regexImg.Replace(subInfoBase, string.Empty);

                    subInfo = Utils.TrimString(subInfoBase, "<br>");
                    subInfo = subInfo.Replace("<br><br>", "<br>").Replace("<br><br>", "<br>");
                    subInfo = subInfo.Replace("&nbsp;", " ");
                    subInfo = subTitle + (String.IsNullOrWhiteSpace(subInfo) ? "" : "<br>" + subInfo);
                }

                string subNumCd = tdNodes[1].InnerHtml;
                string subFps = tdNodes[2].InnerHtml;

                string subRating = "0";
                var rtImgNode = tdNodes[3].QuerySelector("img");
                if (rtImgNode != null) subRating = rtImgNode.GetAttribute("title");

                var linkUploader = tdNodes[5].QuerySelector("a");
                string subUploader = linkUploader == null ? "" : linkUploader.InnerHtml;
                string subDownloads = tdNodes[6].InnerHtml;

                DateTime? dt = null;
                try
                {
                    dt = DateTime.Parse(subDate, CultureInfo.CreateSpecificCulture("bg-BG"));
                    subDate = dt?.ToString("g", CultureInfo.CurrentCulture);
                }
                catch (Exception)
                {
                }

                subInfo += string.Format("<br>{0} | {1} | {2}", subDate, subUploader, subFps);

                var subFiles = new List<(string fileName, string fileExt)>();
                var files = await downloader.GetArchiveFiles(subLink, HttpReferer, null, cancellationToken).ConfigureAwait(false);

                int imdbId = 0;
                string subImdb = "";
                foreach (var fitem in files) using (fitem)
                {
                    if (Regex.IsMatch(fitem.Name, @"subsunacs\.net_\d*\.txt"))
                    {
                        fitem.Content.Seek(0, System.IO.SeekOrigin.Begin);
                        var reader = new System.IO.StreamReader(fitem.Content, Encoding.UTF8, true);
                        string info_text = reader.ReadToEnd();

                        var regexImdbId = new Regex(@"imdb.com/title/(tt(\d+))/?");
                        var match = regexImdbId.Match(info_text);
                        if (match.Success && match.Groups.Count > 2)
                        {
                            subImdb = match.Groups[1].ToString();
                            imdbId = int.Parse(match.Groups[2].ToString());
                        }
                    }
                    else
                    {
                        string file = fitem.Name;
                        string fileExt = fitem.Ext.ToLower();
                        if (fileExt == "txt" && Regex.IsMatch(file, @"subsunacs\.net|танете част|прочети|^read ?me|procheti|Info\.txt", RegexOptions.IgnoreCase)) continue;
                        if (fileExt != "srt" && fileExt != "sub" && fileExt != "txt") continue;

                        subFiles.Add((fitem.Name, fitem.Ext));
                    }
                }

                if (!si.MatchImdbId(imdbId, ref subScoreBase))
                {
                    //_logger.LogInformation($"{NAME}: Ignore result {subImdb} {subTitle} not matching IMDB ID");
                    //continue;
                }

                si.MatchFps(subFps, ref subScoreBase);

                foreach (var (file, fileExt) in subFiles)
                {
                    bool scoreVideoFileName = subFiles.Count == 1 && subInfoBase.ContainsIgnoreCase(si.FileName);
                    bool ignorMutliDiscSubs = subFiles.Count > 1;

                    float score = si.CaclScore(file, subScoreBase, scoreVideoFileName, ignorMutliDiscSubs);
                    if (score == 0 || score < Plugin.Instance.Configuration.MinScore)
                    {
                        _logger.LogInformation($"{NAME}: Ignore file: {file}");
                        continue;
                    }

                    var item = new SubtitleInfo
                    {
                        ThreeLetterISOLanguageName = si.LanguageInfo.ThreeLetterISOLanguageName,
                        Id = Download.GetId(subLink, file, si.LanguageInfo.TwoLetterISOLanguageName, subFps),
                        ProviderName = Name,
                        Name = $"<a href='{subLink}' target='_blank' is='emby-linkbutton' class='button-link' style='margin:0;'>{file}</a>",
                        Format = fileExt,
                        Author = subUploader,
                        Comment = subInfo + " | Score: " + score.ToString("0.00", CultureInfo.InvariantCulture) + " %",
                        DateCreated = dt,
                        CommunityRating = float.Parse(subRating, CultureInfo.InvariantCulture),
                        DownloadCount = int.Parse(subDownloads),
                        IsHashMatch = score >= Plugin.Instance.Configuration.HashMatchByScore,
                        IsForced = false,
                        Score = score,
                    };

                    res.Add(item);
                }
            }

            return res;
        }

    }
}
