﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using Examine;
using Examine.Providers;
using Newtonsoft.Json.Linq;
using Umbraco.Core;
using Umbraco.Core.IO;
using Umbraco.Core.Services;
using Umbraco.Web.HealthCheck;

namespace Our.Umbraco.HealthChecks.Checks.Media
{
    [HealthCheck(
        "d6af141b-d330-4db1-a35f-5cbefd85d04a",
        "Media Integrity Check",
        Description = "Check for any orphaned Media on disk, please ensure you have performed a full site republish and rebuilt your examine indexes before running this test, you can republish by visiting: /umbraco/dialogs/republish.aspx?xml=true and clicking republish entire site",
        Group = "Media")]
    public class MediaIntegrityCheck : HealthCheck
    {
        private readonly ILocalizedTextService _textService;
        private const string MoveOrphanedMediaAction = "moveOrphanedMedia";
        private readonly string _webRoot;
        private readonly string _umbracoConfigPath;
        private readonly BaseSearchProvider _internalIndex;
        private readonly string _umbracoLocalTempStorage;
        public MediaIntegrityCheck(HealthCheckContext healthCheckContext) : base(healthCheckContext)
        {
            _textService = healthCheckContext.ApplicationContext.Services.TextService;
            _umbracoLocalTempStorage = ConfigurationManager.AppSettings["umbracoLocalTempStorage"];
            _webRoot = IOHelper.MapPath("/");
            _internalIndex = ExamineManager.Instance.SearchProviderCollection["InternalSearcher"];
            _umbracoConfigPath = GetUmbracoConfigPath();
        }

        #region Discovery
        /// <summary>
        /// Checks the umbracoLocalTempStorage app setting to determine where the Umbraco.config file is
        /// </summary>
        /// <returns>String containing the full path to the umbraco.config</returns>
        private string GetUmbracoConfigPath()
        {
            string umbracoXMLConfigPath ="";
            //Default location
            if (_umbracoLocalTempStorage == "EnvironmentTemp")
            {
                //TODO: Try checking temp directory
                var appDomainHash = HttpRuntime.AppDomainAppId.ToSHA1();
                var cachePath = Path.Combine(Environment.ExpandEnvironmentVariables("%temp%"), "UmbracoData",
                    //include the appdomain hash is just a safety check, for example if a website is moved from worker A to worker B and then back
                    // to worker A again, in theory the %temp%  folder should already be empty but we really want to make sure that its not
                    // utilizing an old path
                    appDomainHash); 
                umbracoXMLConfigPath = Path.Combine(cachePath, "umbraco.config");
            }
            else if (_umbracoLocalTempStorage == "AspNetTemp")
            {
                var aspNetPath = System.Web.HttpRuntime.CodegenDir;
                umbracoXMLConfigPath = Path.Combine(aspNetPath, "UmbracoData", "umbraco.config");
            }
            else
            {
                umbracoXMLConfigPath = IOHelper.MapPath("/App_Data/umbraco.config");
            }
            
            return umbracoXMLConfigPath;
        }
        
        /// <summary>
        /// Will query the Examine InternalIndex for media items
        /// A media item has the key __IndexType with the value of media
        /// So we query for items in the index using this key and were able to pull all media from examine
        /// We then apply some regex to grab just the path to the media item and store it in a hashset
        /// </summary>
        /// <returns>
        /// A hashset containing paths to media or an empty hashset if nothing is found
        /// </returns>
        private Dictionary<string, HashSet<string>> QueryMediaFromInternalIndex()
        {
            Dictionary<string, HashSet<string>> results = new Dictionary<string, HashSet<string>>();
            HashSet<string> mediaItems = new HashSet<string>();
            HashSet<string> badMediaItemsIDs = new HashSet<string>();
            var searchCriteria = _internalIndex.CreateSearchCriteria();
            var query = searchCriteria.RawQuery("+__IndexType:media +__Icon: icon-picture");
            var searchResults = _internalIndex.Search(query);
            int mediaNotUploadedItems = 0;
            if (searchResults.Any())
            {
                foreach (var itemResult in searchResults)
                {
                    //Neeed to ensure the examine entry has this key before accessing it
                    if (itemResult.Fields.ContainsKey("umbracoFile"))
                    {
                        // \/[M|m]edia\/[0-9]+\/([^']+)
                        // This regex pattern will match the media path within the umbracoFile entry in the examine search {src: '/media/1050/myimage.jpg', crops: []}
                        // In the above example /media/1050/myimage.jpg will be extracted from the entry
                        mediaItems.Add(Regex.Match(itemResult.Fields["umbracoFile"], @"[M|m]edia\/[0-9]+\/([^'|""]+)").Value);
                    }
                    else
                    {
                        //Cause is where a media item exists in Umbraco but no physical piece of media has been attached
                        //TODO: Handle bad entries what data can we obtain from it? Maybe use the database for these
                        mediaNotUploadedItems += 1;
                        if (itemResult.Fields.ContainsKey("__NodeId"))
                        {
                            badMediaItemsIDs.Add(itemResult.Fields["__NodeId"]);
                        }
                        else if (itemResult.Fields.ContainsKey("id"))
                        {
                            badMediaItemsIDs.Add(itemResult.Fields["id"]);
                        }
                    }
                }
                results.Add("mediaItems", mediaItems);
                results.Add("badMediaItemsIDs", badMediaItemsIDs);
                return results;
            }
            return new Dictionary<string, HashSet<string>>();
        }
        /// <summary>
        /// Performs a regex query on the Umbraco.config to look for media file paths in the content
        /// </summary>
        /// <returns>A List<string> containing paths to media found in the Umbraco config</string></returns>
        private List<string> findMediaUrlsInUmbracoConfig()
        {
            XmlDocument doc = new XmlDocument();
            if (File.Exists(_umbracoConfigPath))
            {
                doc.Load(_umbracoConfigPath);
                string umbracoContent = doc.OuterXml;

                MatchCollection matchList = Regex.Matches(umbracoContent, @"[M|m]edia\/[0-9]+\/([^'|"" |\]|\)]+)");
                return matchList.Cast<Match>().Select(match => match.Value).ToList();
            }
            return new List<string>();
        }
        #endregion
        #region Disk Checking
        /// <summary>
        /// Obtains the location of the Media folder then scans and stores all file paths inside a HashSet
        /// </summary>
        /// <returns>HashSet of all items discovered in the media folder</returns>
        private HashSet<string> ScanMediaOnDisk()
        {
            //TODO: Get name of media folder as defined in the FileSystemProviders.config in case a site has a different name for their media
            //TODO: Test with site using a Virtual Directory in IIS (This will probably fail.)
            // Will fail if the media folder is lowercase
            DirectoryInfo mediaDirectory = new DirectoryInfo(IOHelper.MapPath("/Media"));
            //Get Full Path for Media so we can clean the path strings that are placed into the hashset
            string mediaDirectoryString = mediaDirectory.ToString().Replace("Media", "");
            if (!mediaDirectory.Exists)
            {
                //Return empty Hashset when the media directory is missing
                return new HashSet<string>();
            }

            DirectoryInfo[] mediaFolders = mediaDirectory.GetDirectories();

            if (mediaFolders.Length == 0)
            {
                //Return empty Hashset when the media directory is empty
                return new HashSet<string>();
            }

            var mediaFiles = new HashSet<string>();

            foreach (string file in Directory.EnumerateFiles(mediaDirectory.FullName, "*.*", SearchOption.AllDirectories))
            {
                StringBuilder filePath = new StringBuilder(file);
                //Remove web root path from media path
                filePath.Replace(mediaDirectoryString, "");
                //Change backslash to forward slash in path
                filePath.Replace("\\", "/");
                filePath.Replace("Media", "media");
                string cleanedFilePath = filePath.ToString();
                //Path must start with Media or media followed by a / then any length of number then a / followed by any number of characters
                if (Regex.IsMatch(cleanedFilePath, @"^[M|m]edia\/[0-9]+\/.+"))
                {
                    mediaFiles.Add(cleanedFilePath);
                }

            }

            return mediaFiles;
        }

        #endregion

        #region Healthchecks
        /// <summary>
        /// Takes a hashset of media item paths from disk and queries these paths against the Examine Internal Index and umbraco.config
        /// TODO: Figure out a way to accurately query the Examine Index without using a query that starts with a wildcard (Examine would be a better alternative to the XML cache) but is still fine to use
        /// </summary>
        /// <returns>HealthCheck Status for this check, results are passed via ActionParameters </returns>
        private HealthCheckStatus CheckMediaIntegrity()
        {
            bool success = false;
            string resultMessage = string.Empty;
            int foundInMedia = 0;
            HashSet<string> mediaOnDisk = ScanMediaOnDisk();
            //If our media on disk hashset is not empty
            var actions = new List<HealthCheckAction>();
            if (mediaOnDisk.Any())
            {
                Dictionary<string, HashSet<string>> mediaExamineDictionary = QueryMediaFromInternalIndex();
                HashSet<string> mediaInIndex = mediaExamineDictionary["mediaItems"];
                HashSet<string> badMediaItemsIds = mediaExamineDictionary["badMediaItemsIDs"];
                HashSet<string> orphanedMediaItems = new HashSet<string>();
                var mediaItemsInCache = findMediaUrlsInUmbracoConfig();
                foreach (var matchedUmbracoContent in mediaItemsInCache)
                {
                    //Hashset can't contain duplicates so will remove them from the results
                    mediaInIndex.Add(matchedUmbracoContent);
                }
                foreach (var item in mediaOnDisk)
                {

                    if (!mediaInIndex.Contains(item))
                    {
                        orphanedMediaItems.Add(item);
                    }
                    else
                    {
                        //Found in Internal Index
                        foundInMedia += 1;
                    }
                }
                resultMessage = String.Format("Found {0} media items on disk. I found {1} items within the Umbraco Cache, {2} items when combining the cache and examine, {3} items appear to be orphaned, I have found {4} media items that have no media uploaded", mediaOnDisk.Count().ToString(), mediaItemsInCache.Count.ToString(), foundInMedia, orphanedMediaItems.Count.ToString(), badMediaItemsIds.Count.ToString());

                success = orphanedMediaItems.Count == 0;
                //If we find orphaned media we want to add the action button
                if (!success)
                {
                    var parameters = new Dictionary<string, object>();
                    parameters.Add("flaggedMediaItems", orphanedMediaItems);
                    actions.Add(new HealthCheckAction(MoveOrphanedMediaAction, Id)
                    {
                        Name = _textService.Localize("Our.Umbraco.HealthChecks/moveOrphanedMedia"),
                        Description = _textService.Localize("Our.Umbraco.HealthChecks/orphanedMediaDescription"),
                        ActionParameters = parameters,
                    });
                } 
            }
            else
            {
                resultMessage = "We did not find any media on disk. Is the media folder missing or empty on disk?";
                success = true;
            }

            return
                new HealthCheckStatus(resultMessage)
                {
                    ResultType = success ? StatusResultType.Success : StatusResultType.Error,
                    Actions = actions
                };
        }

        #endregion

        #region HealthCheck Action
        /// <summary>
        /// Grab our orphaned media from ActionParameters and cast to a JArray
        /// so it can be iterated in MoveOrphanedMedia
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        public override HealthCheckStatus ExecuteAction(HealthCheckAction action)
        {
            switch (action.Alias)
            {
                case MoveOrphanedMediaAction:
                    JArray flaggedMedia = (JArray)action.ActionParameters["flaggedMediaItems"];
                    return MoveOrphanedMedia(flaggedMedia);
                default:
                    throw new InvalidOperationException("Move Orphaned Media action requested is either not executable or does not exist");
            }
        }

        /// <summary>
        /// Take our flaggedMediaItems and move them outside the web root into a folder named with a DateTime
        /// </summary>
        /// <param name="flaggedMediaItems"></param>
        /// <returns></returns>
        private HealthCheckStatus MoveOrphanedMedia(JArray flaggedMediaItems)
        {
            //TODO: Set to false when we run into an issue
            bool success = true;
            DirectoryInfo mediaFolder = new DirectoryInfo(IOHelper.MapPath("/Media"));

            if (!mediaFolder.Exists)
            {
                throw new DirectoryNotFoundException();
            }

            //Attempt to create a folder outside the web root
            DirectoryInfo webRoot = new DirectoryInfo(_webRoot);
            string targetMediaFolderName = "badMedia-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
            DirectoryInfo mediaDeposit = new DirectoryInfo(webRoot.Parent.FullName + "\\media-cleaner\\"+ targetMediaFolderName);
            mediaDeposit.Create();
            foreach (string item in flaggedMediaItems)
            {
                string source = IOHelper.MapPath("~/"+item);
                string destination = mediaDeposit.FullName + "\\" + Regex.Replace(item,@"/",@"\");

                DirectoryInfo file = new DirectoryInfo(Path.GetDirectoryName(destination));
                file.Create();

                File.Move(source, destination);
            }

            string message = _textService.Localize("Our.Umbraco.HealthChecks/moveOrphanedMediaSuccess");

            return
                    new HealthCheckStatus(message)
                    {
                        ResultType = success? StatusResultType.Success: StatusResultType.Error
                    };

            /*
             * TODO: Log error condition here 
             */
            //return
            //        new HealthCheckStatus(_textService.Localize("Our.Umbraco.HealthChecks/moveOrphanedMediaError"))
            //        {
            //            ResultType = StatusResultType.Error
            //        };
        }

        public override IEnumerable<HealthCheckStatus> GetStatus()
        {
            //return the statuses
            return new[] { CheckMediaIntegrity() };
        }

        #endregion
    }
}
