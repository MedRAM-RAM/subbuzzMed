﻿using AngleSharp;
using AngleSharp.Html.Parser;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using subbuzz.Configuration;
using subbuzz.Extensions;
using subbuzz.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace subbuzz.Providers
{
    public class Subf2m : ISubBuzzProvider
    {
        internal const string NAME = "Subf2m";
        private const string ServerUrl = "https://subf2m.co";
        private static readonly string[] CacheRegionSub = { "subf2m", "sub" };
        private static readonly string[] CacheRegionSubPage = { "subf2m", "subpage" };
        private static readonly string[] CacheRegionSearch = { "subf2m", "search" };

        private readonly Logger _logger;
        private readonly Http.Download _downloader;
        private readonly IFileSystem _fileSystem;
        private readonly ILocalizationManager _localizationManager;
        private readonly ILibraryManager _libraryManager;

        private static readonly Dictionary<string, string> _languages = new Dictionary<string, string>
        {
            { "alb", "albanian" }, { "ara", "arabic" }, { "arm", "armenian" }, { "aze", "azerbaijani" },
            { "baq", "basque" }, { "bel", "belarusian" }, { "ben", "bengali" }, { "bos", "bosnian" }, { "bul", "bulgarian" }, { "bur", "burmese" },
            { "cat", "catalan" }, { "chs", "chinese-bg-code" }, { "cht", "chinese-bg-code" }, { "chi", "chinese-bg-code" }, { "cze", "czech" },
            { "dan", "danish" }, { "dut", "dutch" },
            { "eng", "english" }, { "epo", "esperanto" }, { "est", "estonian" },
            { "fin", "finnish" }, { "fre", "french" }, { "geo", "georgian" }, { "ger", "german" }, { "gre", "greek" },
            { "heb", "hebrew" }, { "hin", "hindi" }, { "hrv", "croatian" }, { "hun", "hungarian" },
            { "ice", "icelandic" }, { "ind", "indonesian" }, { "ita", "italian" },
            { "jav", "japanese" },
            { "kan", "kannada" }, { "kin", "kinyarwanda" }, { "kor", "korean" }, { "kur", "kurdish" },
            { "lav", "latvian" }, { "lit", "lithuanian" },
            { "mac", "macedonian" }, { "mal", "malayalam" }, { "may", "malay" }, { "mon", "mongolian" },
            { "nep", "nepali" }, { "nno", "norwegian" }, { "nob", "norwegian" }, { "nor", "norwegian" },
            { "per", "farsi_persian" }, { "pob", "brazillian-portuguese" }, { "pol", "polish" }, { "por", "portuguese" },
            { "rum", "romanian" }, { "rus", "russian" },
            { "sin", "sinhala" }, { "slo", "slovak" }, { "slv", "slovenian" }, { "som", "somali" }, { "spa", "spanish" },
            { "srp", "serbian" }, { "sun", "sundanese" }, { "swa", "swahili" }, { "swe", "swedish" },
            { "tam", "tamil" }, { "tel", "telugu" }, { "tgl", "tagalog" }, { "tha", "thai" }, { "tur", "turkish" },
            { "ukr", "ukrainian" }, { "urd", "urdu" },
            { "vie", "vietnamese" },
            { "yor", "yoruba" },
        };

        private static readonly string[] _seasonNumbers = {
            "",
            "First",        "Second",       "Third",        "Fourth",       "Fifth",
            "Sixth",        "Seventh",      "Eighth",       "Ninth",        "Tenth",
            "Eleventh",     "Twelfth",      "Thirteenth",   "Fourteenth",   "Fifteenth",
            "Sixteenth",    "Seventeenth",  "Eighteenth",   "Nineteenth",   "Twentieth",
        };

        private static readonly Regex ImdbUrlRegex = new Regex(@"imdb.com/title/tt(?<imdbid>\d{7,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SubDetailsRegex = new Regex(@"(?<par>\w[\w|\s]+)\:(?:\s*)(?<val>[^\n\t]*\S)(?:\s*)(?<val2>[^\n\t]*\S)?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        public string Name => $"[{Plugin.NAME}] <b>{NAME}</b>";

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new List<VideoContentType> { VideoContentType.Episode, VideoContentType.Movie };

        private PluginConfiguration GetOptions()
            => Plugin.Instance.Configuration;

        public class InvalidHtmlException : Exception
        {
            public string Body { get; set; }
            public InvalidHtmlException(string msg, string body) : base(msg) { Body = body; }
        }

        public Subf2m(
            Logger logger,
            IFileSystem fileSystem,
            ILocalizationManager localizationManager,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _localizationManager = localizationManager;
            _libraryManager = libraryManager;
            _downloader = new Http.Download(logger);

        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            return await _downloader.GetSubtitles(id, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request,
            CancellationToken cancellationToken)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var res = new List<SubtitleInfo>();

            try
            {
                if (!GetOptions().EnableSubf2m)
                {
                    // provider is disabled
                    return res;
                }

                string seasonFormat = string.Empty;
                if (request.ParentIndexNumber != null &&
                    request.ParentIndexNumber < _seasonNumbers.Length)
                {
                    seasonFormat = $"{"{0}"} - {_seasonNumbers[request.ParentIndexNumber ?? 0]} Season";
                }

                SearchInfo si = SearchInfo.GetSearchInfo(request, _localizationManager, _libraryManager, seasonFormat);
                _logger.LogInformation($"Request subtitle for '{si.SearchText}', language={si.Lang}, year={request.ProductionYear}");

                string langPage;
                if (!_languages.TryGetValue(si.LanguageInfo.ThreeLetterISOLanguageName, out langPage))
                {
                    _logger.LogInformation($"Language not supported: {si.LanguageInfo.ThreeLetterISOLanguageName}");
                    return res;
                }

                if (si.SearchText.IsNullOrWhiteSpace() || si.ImdbIdInt <= 0)
                {
                    _logger.LogInformation($"Search info or IMDB ID missing");
                    return res;
                }

                var url = $"{ServerUrl}/subtitles/searchbytitle?query={HttpUtility.UrlEncode(si.SearchText)}&l=";
                res = await SearchUrl(url, si, langPage, cancellationToken).ConfigureAwait(false);

            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Search error: {e}");
            }

            watch.Stop();
            _logger.LogInformation($"Search duration: {watch.ElapsedMilliseconds/1000.0} sec. Subtitles found: {res.Count}");

            return res;
        }

        protected async Task<List<SubtitleInfo>> SearchUrl(string url, SearchInfo si, string langPage, CancellationToken cancellationToken, int retry = 0)
        {
            try
            {
                var link = new Http.RequestCached
                {
                    Url = url,
                    Referer = ServerUrl,
                    Type = Http.RequestType.GET,
                    CacheRegion = CacheRegionSearch,
                    CacheLifespan = GetOptions().Cache.GetSearchLife(),
                };

                using (var resp = await _downloader.GetResponse(link, cancellationToken).ConfigureAwait(false))
                {
                    var res = await ParseSearchResult(resp.Content, si, langPage, cancellationToken);
                    _downloader.AddResponseToCache(link, resp);
                    return res;
                }
            }
            catch (InvalidHtmlException e)
            {
                if (retry < 3 && await HandleJavascriptChallenge(e.Body))
                    return await SearchUrl(url, si, langPage, cancellationToken, retry++);

                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"GET: {url}: SearchUrl: {e}");
                return new List<SubtitleInfo>();
            }
        }

        protected async Task<List<SubtitleInfo>> ParseSearchResult(System.IO.Stream html, SearchInfo si, string langPage, CancellationToken cancellationToken)
        {
            var res = new List<SubtitleInfo>();

            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            var parser = new HtmlParser(context);
            var htmlDoc = parser.ParseDocument(html);

            var resDiv = htmlDoc.QuerySelector("div.search-result");
            if (resDiv == null)
                throw new InvalidHtmlException("Invalid HTML, div.search-result not found", htmlDoc.Body.TextContent);

            var resUl = resDiv.QuerySelector("h2.exact")?.NextElementSibling;

            if (resUl == null)
                resUl = resDiv.QuerySelector("h2.close")?.NextElementSibling;

            if (resUl == null)
            {
                if (si.VideoType == VideoContentType.Movie)
                {
                    resUl = resDiv.QuerySelector("h2.popular")?.NextElementSibling;
                }
            }

            if (resUl == null)
                throw new Exception("Invalid HTML, tag not found");

            var listItems = resUl.QuerySelectorAll("li");
            foreach (var li in listItems)
            {
                var link = li.QuerySelector("a");
                if (link == null) continue;

                res = await GetSubtitlesListPage(ServerUrl + link.GetAttribute("href") + $"/{langPage}", si, cancellationToken);
                if (res.Count > 0) return res;
            }

            return res;
        }

        protected async Task<List<SubtitleInfo>> GetSubtitlesListPage(string url, SearchInfo si, CancellationToken cancellationToken, int retry = 0)
        {
            try
            {
                var link = new Http.RequestCached
                {
                    Url = url,
                    Referer = ServerUrl,
                    Type = Http.RequestType.GET,
                    CacheRegion = CacheRegionSearch,
                    CacheLifespan = GetOptions().Cache.GetSearchLife() 
                };

                using (var resp = await _downloader.GetResponse(link, cancellationToken))
                {
                    var res = await ParseSubtitlesList(resp.Content, si, cancellationToken).ConfigureAwait(false);
                    _downloader.AddResponseToCache(link, resp);
                    return res;
                }
            }
            catch (InvalidHtmlException e)
            {
                if (retry < 3 && await HandleJavascriptChallenge(e.Body))
                    return await GetSubtitlesListPage(url, si, cancellationToken, retry++);

                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"GET: {url}: GetSubtitlesListPage: {e}");
                return new List<SubtitleInfo>();
            }
        }

        protected class SubData
        {
            public List<string> Releases = new List<string>();
            public string Uploader = string.Empty;
            public string Comment = string.Empty;
        };

        protected async Task<List<SubtitleInfo>> ParseSubtitlesList(System.IO.Stream html, SearchInfo si, CancellationToken cancellationToken)
        {
            var res = new List<SubtitleInfo>();
            var links = new Dictionary<string, SubData>();

            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            var parser = new HtmlParser(context);
            var htmlDoc = parser.ParseDocument(html);

            var imdbTag = htmlDoc.QuerySelector("a.imdb");
            if (imdbTag == null)
                throw new InvalidHtmlException("Invalid HTML, a.imdb not found", htmlDoc.Body.TextContent);

            var imdbLink = imdbTag.GetAttribute("href");
            var imdbMatch = ImdbUrlRegex.Match(imdbLink);
            if (imdbMatch == null || !imdbMatch.Success)
                throw new Exception("Invalid HTML, a.imdb[href] not found");

            _ = int.TryParse(imdbMatch.Groups["imdbid"].Value, out int imdbId);
            if (imdbId <= 0 || (si.ImdbIdInt != imdbId && si.ImdbIdEpisodeInt != imdbId))
                return res;

            var tbl = htmlDoc.QuerySelector("ul.sublist");
            var trs = tbl?.QuerySelectorAll("li.item");
            foreach (var tr in trs)
            {
                try
                {
                    var tagInfo = tr.QuerySelector("div.col-info");
                    if (tagInfo == null) 
                        continue;

                    var tagUl = tagInfo.QuerySelector("ul.scrolllist");
                    var tagLi = tagUl?.QuerySelectorAll("li");
                    if (tagLi == null || tagLi.Length < 1) 
                        continue;
                    
                    var releases = new List<string>();
                    foreach (var item in tagLi)
                    {
                        releases.Add(item.InnerHtml.Trim());
                    }

                    var tagLink = tr.QuerySelector("a.download");
                    if (tagLink == null || !tagLink.HasAttribute("href")) continue;
                    var subLink = ServerUrl + tagLink.Attributes["href"].Value;

                    if (!links.ContainsKey(subLink))
                    {
                        links[subLink] = new SubData();

                        var tagComment = tr.QuerySelector("div.comment-col");

                        var uploader = tagComment.QuerySelector("a")?.TextContent.Trim(new char[] { ' ', '\t', '\n' });
                        links[subLink].Uploader = uploader.IsNotNullOrWhiteSpace() ? uploader : "Anonymous";

                        var comment = tagComment.QuerySelector("p")?.TextContent;
                        if (comment != null) links[subLink].Comment = comment;
                    }

                    links[subLink].Releases.AddRange(releases);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Parsing subtitles list error: {e}");
                }
            }

            _logger.LogDebug($"Loading subtitle information for {links.Count} items");
            var tasks = new List<Task<List<SubtitleInfo>>>();

            foreach (var link in links)
            {
                try
                {
                    if (si.VideoType == VideoContentType.Episode)
                    {
                        bool skip = true;
                        foreach (var rel in link.Value.Releases)
                        {
                            Parser.EpisodeInfo epInfo = Parser.Episode.ParseTitle(rel);
                            if (epInfo == null) continue;

                            if (epInfo.SeasonNumber != si.SeasonNumber) continue;
                            if (epInfo.EpisodeNumbers == null || epInfo.EpisodeNumbers.Length <= 0)
                            {
                                // season pack
                                skip = false;
                                break;
                            }

                            if (Array.Find(epInfo.EpisodeNumbers, x => x == si.EpisodeNumber) == si.EpisodeNumber)
                            {
                                skip = false;
                                break;
                            }
                        }

                        if (skip)
                        {
                            _logger.LogInformation($"Skipping: {link.Key} - {link.Value.Releases[0]}");
                            continue;
                        }
                    }

                    tasks.Add(GetSubtitlePage(link.Key, link.Value, si, cancellationToken));
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Parsing subtitles {link.Key} error: {e}");
                }
            }

            while (tasks.Count > 0)
            {
                var idx = Task.WaitAny(tasks.ToArray());
                if (idx < 0)
                    return res;

                try
                {
                    res.AddRange(await tasks[idx].ConfigureAwait(false));
                }
                catch (TaskCanceledException e)
                {
                    _logger.LogError(e, $"ParseSubtitlesList Timeout error: {e}");
                    return res;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"ParseSubtitlesList wait for task error: {e}");
                }

                tasks.RemoveAt(idx);
            }

            return res;
        }

        protected async Task<List<SubtitleInfo>> GetSubtitlePage(string url, SubData subData, SearchInfo si, CancellationToken cancellationToken, int retry = 0)
        {
            try
            {
                var link = new Http.RequestCached
                {
                    Url = url,
                    Referer = ServerUrl,
                    Type = Http.RequestType.GET,
                    CacheRegion = CacheRegionSubPage,
                    CacheLifespan = GetOptions().Cache.GetSubLife(),
                };

                using (var resp = await _downloader.GetResponse(link, cancellationToken).ConfigureAwait(false))
                {
                    var res = await ParseSubtitlePage(resp.Content, url, subData, si, cancellationToken);
                    if (res.Count > 0)
                        _downloader.AddResponseToCache(link, resp);
                    return res;
                }
            }
            catch (InvalidHtmlException e)
            {
                if (retry < 3 && await HandleJavascriptChallenge(e.Body))
                    return await GetSubtitlePage(url, subData, si, cancellationToken, retry++);

                throw;
            }
            catch
            {
                throw;
            }
        }

        protected async Task<List<SubtitleInfo>> ParseSubtitlePage(System.IO.Stream html, string urlPage, SubData subData, SearchInfo si, CancellationToken cancellationToken)
        {
            var res = new List<SubtitleInfo>();

            var subScoreBase = new SubtitleScore();
            subScoreBase.AddMatch("imdb");

            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            var parser = new HtmlParser(context);
            var htmlDoc = parser.ParseDocument(html);

            var tagSubs = htmlDoc.QuerySelector("div.subtitle");
            var tagHeader = tagSubs?.QuerySelector("div.header");
            var tagDetails = tagSubs?.QuerySelectorAll("div.details ul > li");

            var downloadLink = tagHeader?.QuerySelector("div.download")?.QuerySelector("a")?.GetAttribute("href");
            if (downloadLink.IsNullOrWhiteSpace())
                throw new InvalidHtmlException("Invalid HTML, download link not found", htmlDoc.Body.TextContent);

            downloadLink = ServerUrl + downloadLink;

            string title = tagHeader?.QuerySelector("span[itemprop='name']")?.TextContent.Trim();
            string subInfo = title.IsNotNullOrWhiteSpace() ? title : string.Empty;
            subInfo += "<br>" + string.Join("<br>", subData.Releases.ToArray());

            bool? isForced = null;
            bool? isSdh = null;
            int numFiles = 0;
            int subDownloads = 0;
            float fps = 0;
            DateTime? dt = null;
            string subDate = string.Empty;
            if (tagDetails != null)
                foreach (var tag in tagDetails)
                {
                    var match = SubDetailsRegex.Match(tag.TextContent);
                    if (!match.Success) continue;
                    switch (match.Groups["par"].Value)
                    {
                        case "Online":
                            try
                            {
                                dt = DateTime.Parse(match.Groups["val"].Value, CultureInfo.InvariantCulture);
                                subDate = dt?.ToString("g", CultureInfo.CurrentCulture);
                            }
                            catch (Exception)
                            {
                            }
                            break;

                        case "Hearing Impaired":
                            isSdh = match.Groups["val"].Value.EqualsIgnoreCase("yes");
                            break;

                        case "Foreign parts":
                            isForced = match.Groups["val"].Value.EqualsIgnoreCase("Foreign parts only");
                            break;

                        case "Framerate":
                            _ = float.TryParse(match.Groups["val"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out fps);
                            break;

                        case "Files":
                            var parts = match.Groups["val"].Value.Split('(');
                            if (parts.Length > 0)
                                _ = int.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out numFiles);
                            break;

                        case "Rated":
                            break;

                        case "Downloads":
                            _ = int.TryParse(match.Groups["val"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out subDownloads);
                            break;
                    }
                }

            subInfo += subData.Comment.IsNotNullOrWhiteSpace() ? $"<br>{subData.Comment}" : "";
            subInfo += $"<br>{subDate} | {subData.Uploader}";

            if (subData.Releases.Count == 1)
                si.MatchTitle(subData.Releases[0], ref subScoreBase);

            var link = new Http.RequestSub
            {
                Url = downloadLink,
                Referer = ServerUrl,
                Type = Http.RequestType.GET,
                CacheKey = urlPage,
                CacheRegion = CacheRegionSub,
                CacheLifespan = GetOptions().Cache.GetSubLife(),
                Lang = si.GetLanguageTag(),
                Fps = fps < 1 ? null : fps,
                FpsVideo = si.VideoFps,
            };

            using (var files = await _downloader.GetArchiveFiles(link, cancellationToken).ConfigureAwait(false))
            {
                int subFilesCount = files.SubCount;

                foreach (var file in files)
                {
                    if (!file.IsSubfile())
                    {
                        _logger.LogDebug($"Ignoring '{file.Name}' as it's not a subtitle file. Page: {urlPage}");
                        continue;
                    }

                    link.File = file.Name;
                    link.Fps = file.Sub.FpsRequested;
                    link.IsSdh = isSdh;
                    link.IsForced = isForced;

                    string subFpsInfo = link.Fps == null ? "" : link.Fps?.ToString(CultureInfo.InvariantCulture);
                    if (file.Sub.FpsRequested != null && file.Sub.FpsDetected != null &&
                        Math.Abs(file.Sub.FpsRequested ?? 0 - file.Sub.FpsDetected ?? 0) > 0.001)
                    {
                        subFpsInfo = $"{file.Sub.FpsRequested?.ToString(CultureInfo.InvariantCulture)} ({file.Sub.FpsDetected?.ToString(CultureInfo.InvariantCulture)})";
                        link.Fps = file.Sub.FpsDetected;
                    }

                    var subFileInfo = subInfo;
                    if (subFpsInfo.IsNotNullOrWhiteSpace())
                        subFileInfo += $" | {subFpsInfo}";

                    SubtitleScore subScore = (SubtitleScore)subScoreBase.Clone();
                    si.MatchFps(link.Fps, ref subScore);

                    bool scoreVideoFileName = subFilesCount == 1 && subData.Releases.Find(x => x.EqualsIgnoreCase(si.FileName)).IsNotNullOrWhiteSpace();
                    bool ignorMutliDiscSubs = subFilesCount > 1;

                    float score = si.CaclScore(file.Name, subScore, scoreVideoFileName, ignorMutliDiscSubs);
                    if (score == 0 || score < GetOptions().MinScore)
                    {
                        _logger.LogInformation($"Ignore file: {file.Name} Page: {urlPage} Socre: {score}");
                        continue;
                    }

                    var subItmem = new SubtitleInfo
                    {
                        ThreeLetterISOLanguageName = si.LanguageInfo.ThreeLetterISOLanguageName,
                        Id = link.GetId(),
                        ProviderName = Name,
                        Name = file.Name,
                        PageLink = urlPage,
                        Format = file.GetExtSupportedByEmby(),
                        Comment = subFileInfo + " | Score: " + score.ToString("0.00", CultureInfo.InvariantCulture) + " %",
                        DateCreated = dt,
                        DownloadCount = subDownloads,
                        IsHashMatch = score >= GetOptions().HashMatchByScore,
                        IsForced = link.IsForced,
                        IsHearingImpaired = link.IsSdh,
                        Score = score,
                    };

                    res.Add(subItmem);
                }
            }

            return res;
        }

        private async Task<bool> HandleJavascriptChallenge(string html)
        {
            var match = Regex.Match(html, @"eval\(""(.*?)""\),", RegexOptions.Multiline);
            if (!match.Success || match.Groups.Count < 2)
            {
                _logger.LogInformation($"Javascript challenge not found");
                return false;
            }

            _logger.LogDebug("Calculating cookie...");

            var param = $"-e \"console.log('__arcsjs='+eval(\\\"{match.Groups[1].Value}\\\"));\"";

            int exitCode = 0;
            var stdOut = await SimpleExec.Command.ReadAsync(
                "node", param, 
                null, true, null, null, null, null, true, null,
                (code) => { exitCode = code;  return true; }
                );

            if (exitCode == 0)
            {
                var parts = stdOut.Trim().Split('=');
                if (parts.Length == 2)
                {
                    _downloader.AddCookie(new System.Net.Cookie(parts[0], parts[1], "/", ".subf2m.co"));
                    _logger.LogDebug($"Cookie set: {parts[0]}={parts[1]}");
                    return true;
                }
            }

            _logger.LogInformation($"Caclulation of javascript challenge failed! Exit: {exitCode}, Output: {stdOut}");
            return false;
        }
    }
}
