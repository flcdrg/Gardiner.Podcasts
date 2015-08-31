using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Syndication;

namespace Gardiner.Podcasts
{
    public class Feeds
    {
        public IList<SyndicationItem> Thing(string text)
        {
            SyndicationFeed feed = new SyndicationFeed();

            feed.Load(text);

            return feed.Items;
        }

        public async Task<string> GetFeedXml(Uri uri)
        {
            var hash = uri.GetHashCode();

            // get data folder
            var localFolder = ApplicationData.Current.LocalFolder;

            var filename = hash + ".xml";

            var client = new HttpClient();

            StorageFile file;
            try
            {
                file = await localFolder.GetFileAsync(filename);

                var msg = new HttpRequestMessage(HttpMethod.Head, uri);
                msg.Headers.IfModifiedSince = file.DateCreated;

                var resp = await client.SendRequestAsync(msg);

                if (resp.Headers.ContainsKey("Last-Modified"))
                {

                    Debug.WriteLine(resp.Headers["Last-Modified"]);

                }

            }
            catch (FileNotFoundException)
            {
            }

            string response = await client.GetStringAsync(uri);

            file = await localFolder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
            var stream = await file.OpenStreamForWriteAsync();
            var writer = new StreamWriter(stream);
            writer.Write(response);
            writer.Flush();
            writer.Dispose();
            stream.Dispose();
            
            // save to data folder
            return response;
        }

        public async Task<IList<string>> FeedUrlsFromOpml()
        {
            var file = await GetPackagedFile(null, "Podcast+Pro_SubscriptionsOPML_20150705.opml");

            var doc = await XmlDocument.LoadFromFileAsync(file);

            var outlines = doc.SelectNodes("/opml/body/outline");

            return outlines.Select(o => o.Attributes.GetNamedItem("xmlUrl").InnerText).ToList();
        }

        private async Task<StorageFile> GetPackagedFile(string folderName, string fileName)
        {
            StorageFolder installFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            if (folderName != null)
            {
                StorageFolder subFolder = await installFolder.GetFolderAsync(folderName);
                return await subFolder.GetFileAsync(fileName);
            }
            else
            {
                return await installFolder.GetFileAsync(fileName);
            }
        }
    }
}
