using System; // IntPtr.Size
using System.Diagnostics; // Conditional
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading; // Thread.Join()
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using CodePlex.TfsLibrary.ObjectModel;
using SvnBridge.Net;

namespace SvnBridge.Utility
{
	public static class Helper
	{
		private static readonly byte[] _emptyBuffer = new byte[0];
		private static readonly string[] DECODED = new string[] { "%", "#", " ", "^", "{", "[", "}", "]", ";", "`", "&" };
		private static readonly string[] DECODED_B = new string[] { "&", "<", ">" };
		private static readonly string[] DECODED_C = new string[] { "%", "#", " ", "^", "{", "[", "}", "]", ";", "`" };

		private static readonly string[] ENCODED = new string[] { "%25", "%23", "%20", "%5e", "%7b", "%5b", "%7d", "%5d", "%3b", "%60", "&amp;" };
		private static readonly string[] ENCODED_B = new string[] { "&amp;", "&lt;", "&gt;" };
		private static readonly string[] ENCODED_C = new string[] { "%25", "%23", "%20", "%5e", "%7b", "%5b", "%7d", "%5d", "%3b", "%60" };

        public static StreamWriter ConstructStreamWriterUTF8(Stream outputStream)
        {
            Encoding utf8WithoutBOM = new UTF8Encoding(false);

            // Default buffer size is 1024 Bytes, which is rather low
            // for our purpose (ends up as chunk size when using HTTP Chunked Encoding).
            // NOTE that at least Subversion 1.6.17 (neon) appears to be buggy
            // since it seems to have trouble handling incompletely-chunked
            // transfers (however, incomplete payload within individual
            // chunks appears to be completely legal and actually an
            // inherent characteristic of chunking, one could say).
            return new StreamWriter(outputStream, utf8WithoutBOM, 16 * 1024);
        }

		public static XmlReaderSettings InitializeNewXmlReaderSettings()
		{
			XmlReaderSettings readerSettings = new XmlReaderSettings();
			readerSettings.CloseInput = false;
			return readerSettings;
		}

		public static T DeserializeXml<T>(XmlReader reader)
		{
                        // Side note: XmlSerializer is known to be easily leaky.
                        // However, since this class is using "simple"
                        // ctor variants only which don't exhibit such
                        // leaks (see
                        // http://msdn.microsoft.com/en-us/library/system.xml.serialization.xmlserializer.aspx
                        // ), it's no problem here.
			XmlSerializer requestSerializer = new XmlSerializer(typeof(T));
			return (T)requestSerializer.Deserialize(reader);
		}

		public static T DeserializeXml<T>(string xml)
		{
			XmlReader reader = XmlReader.Create(new StringReader(xml), InitializeNewXmlReaderSettings());
			return (T)DeserializeXml<T>(reader);
		}

		public static T DeserializeXml<T>(byte[] xml)
		{
			XmlReader reader = XmlReader.Create(new MemoryStream(xml), InitializeNewXmlReaderSettings());
			return (T)DeserializeXml<T>(reader);
		}

		public static T DeserializeXml<T>(Stream requestStream)
		{
			using (XmlReader reader = XmlReader.Create(requestStream, InitializeNewXmlReaderSettings()))
			{
				return DeserializeXml<T>(reader);
			}
		}

		public static byte[] SerializeXml<T>(T request)
		{
			XmlWriterSettings settings = new XmlWriterSettings();
			settings.CloseOutput = false;
			settings.Encoding = Encoding.UTF8;
			MemoryStream xml = new MemoryStream();
			XmlWriter writer = XmlWriter.Create(xml, settings);
			XmlSerializer serializer = new XmlSerializer(typeof(T));
			XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
			serializer.Serialize(writer, request, ns);
			writer.Flush();
			return xml.ToArray();
		}

		public static string SerializeXmlString(object request)
		{
			StringWriter sw = new StringWriter();
			XmlSerializer serializer = new XmlSerializer(request.GetType());
			XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
			serializer.Serialize(sw, request, ns);
			return sw.GetStringBuilder().ToString();
		}

		public static bool IsValidPort(string port)
		{
			int portAsInt;

			if (!int.TryParse(port, out portAsInt))
			{
				return false;
			}

			return IsValidPort(portAsInt);
		}

		public static bool IsValidPort(int port)
		{
			return (port >= 1 && port <= 65535);
		}

		public static bool IsPortInUseOnLocalHost(int port)
		{
			bool inUse = false;
			TcpListener listener = new TcpListener(IPAddress.Loopback, port);
			try
			{
				listener.Start();
			}
			catch (SocketException)
			{
				inUse = true;
			}
			listener.Stop();
			return inUse;
		}

		public static bool IsValidTFSUrl(string url, ProxyInformation proxyInformation)
		{
			try
			{
				WebRequest request = WebRequest.Create(url + "/Services/v1.0/Registration.asmx");
				// For the simple purpose of checking whether a web service exists,
				// using an unsafe credential ought to be ok
				// (actual use should then be properly using a client-provided credential).
				request.Credentials = GetUnsafeNetworkCredential();
				request.Proxy = CreateProxy(proxyInformation);
				request.Timeout = 60000;

				using (WebResponse response = request.GetResponse())
				using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
				{
					string output = reader.ReadToEnd();
					return (output.Contains("Team Foundation Registration web service"));
				}
			}
			catch (WebException e)
			{
				HttpWebResponse response = e.Response as HttpWebResponse;

				if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
				{
					// we need to ensure that common case of:
					// http://server:80   <- share point
					// htpp://server:8080 <- TFS
					return response.Headers["MicrosoftSharePointTeamServices"] == null;
				}

				return false;
			}
			catch
			{
				return false;
			}
		}

		public static bool IsValidUrl(string url)
		{
			try
			{
				new Uri(url);
				return true;
			}
			catch (UriFormatException)
			{
				return false;
			}
		}

		/// <summary>
		/// Comment-only helper:
		///
		/// WARNING SECURITY NOTE!! whenever using DefaultNetworkCredentials,
		/// we end up using credentials
		/// of the *current* (*application-side*) security context,
		/// i.e. ones that did *not* get supplied by the SVN *client* user
		/// (who may or may not have been able to authenticate properly!).
		/// </summary>
		/// I had intended to use the more appropriate naming of
		/// "insecure" rather than "unsafe",
		/// but then there is
		/// HttpWebRequest.UnsafeAuthenticatedConnectionSharing, so...
		public static NetworkCredential GetUnsafeNetworkCredential()
		{
			return CredentialCache.DefaultNetworkCredentials;
		}

        public static string GetMd5Checksum(Stream data)
        {
            // FIXME: ermm, there is a Stream-parameterized variant
            // of MD5::ComputeHash() as well - why don't we simply
            // make use of that instead,
            // just like the Array-parameterized variant below??
            MD5 md5 = MD5.Create();
            int num;
            byte[] buffer = new byte[0x1000];
            do
            {
                num = data.Read(buffer, 0, buffer.Length);
                if (num > 0)
                {
                    md5.TransformBlock(buffer, 0, num, null, 0);
                }
            }
            while (num > 0);
            md5.TransformFinalBlock(buffer, 0, num);

            return GetMd5ChecksumString(md5.Hash);
        }

        // And a variant for "jagged array" input,
        // suitable to avoid large LOH-destined allocation sizes.
        public static string GetMd5Checksum(byte[][] jagged)
        {
            // Useful discussion:
            //   http://stackoverflow.com/questions/878837/salting-a-c-sharp-md5-computehash-on-a-stream
            MD5 md5 = MD5.Create();
            foreach(var innerArray in jagged)
            {
              md5.TransformBlock(innerArray, 0, innerArray.Length, null, 0);
            }
            md5.TransformFinalBlock(_emptyBuffer, 0, 0);

            return GetMd5ChecksumString(md5.Hash);
        }

		public static string GetMd5Checksum(byte[] data)
		{
			MD5 md5 = MD5.Create();
                        return GetMd5ChecksumString(md5.ComputeHash(data));
		}

        private static string GetMd5ChecksumString(byte[] hash)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2").ToLower());
            }

            return sb.ToString();
        }

		private static string Encode(string[] encoded, string[] decoded, string value, bool capitalize)
		{
			if (value == null)
			{
				return value;
			}

			for (int i = 0; i < decoded.Length; i++)
			{
				if (capitalize && decoded[i] != "&")
				{
					value = value.Replace(decoded[i], encoded[i].ToUpper());
				}
				else
				{
					value = value.Replace(decoded[i], encoded[i]);
				}
			}

			return value;
		}

		private static string Decode(string value, bool capitalize)
		{
			if (value == null)
			{
				return value;
			}

			for (int i = ENCODED.Length - 1; i >= 0; i--)
			{
				if (capitalize)
				{
					value = value.Replace(ENCODED[i].ToUpper(), DECODED[i]);
				}
				else
				{
					value = value.Replace(ENCODED[i], DECODED[i]);
				}
			}

			return value;
		}

		public static string Encode(string value)
		{
			return Encode(value, false);
		}

		public static string Encode(string value, bool capitalize)
		{
			return Encode(ENCODED, DECODED, value, capitalize);
		}

		public static string Decode(string value)
		{
			return Decode(value, false);
		}

		public static string EncodeB(string value)
		{
			return Encode(ENCODED_B, DECODED_B, value, false);
		}

		public static string DecodeB(string value)
		{
			return Decode(value, false);
		}

		public static string EncodeC(string value)
		{
			return Encode(ENCODED_C, DECODED_C, value, true);
		}

		public static string DecodeC(string value)
		{
			return Decode(value, true);
		}

		public static string UrlEncodeIfNecessary(string href)
		{
                        // Hmm... why can't we use the venerable
                        // Uri.EscapeUriString() method instead?
                        // Surely would be much faster, too...
			StringBuilder sb = new StringBuilder();
			foreach (char c in href)
			{
				if (c > 256)
				{
					sb.Append(HttpUtility.UrlEncode(c.ToString()));
				}
				else
				{
					sb.Append(c);
				}
			}
			return sb.ToString();
		}

		public static string FormatDate(DateTime date)
		{
			string result = date.ToUniversalTime().ToString("o");
			return result.Remove(result.Length - 2, 1);
		}

        public static string FormatDateB(DateTime date)
        {
            return date.ToUniversalTime().ToString("R");
        }

		public static IWebProxy CreateProxy(ProxyInformation proxyInformation)
		{
			if (proxyInformation.UseProxy == false)
				return null;
			IWebProxy proxy = new WebProxy(proxyInformation.Url, proxyInformation.Port);
			ICredentials credential;
			if (proxyInformation.UseDefaultCredentials)
			{
				credential = CredentialCache.DefaultNetworkCredentials;
			}
			else
			{
				credential = new NetworkCredential(proxyInformation.Username, proxyInformation.Password);
			}
			proxy.Credentials = credential;
			return proxy;
		}

		public static IList<SourceItemHistory> SortHistories(bool updatingForwardInTime,
														 IEnumerable<SourceItemHistory> items)
		{
			List<SourceItemHistory> histories = new List<SourceItemHistory>(items);

			histories.Sort(delegate(SourceItemHistory x, SourceItemHistory y)
			{
				if (updatingForwardInTime)
				{
					return x.ChangeSetID.CompareTo(y.ChangeSetID);
				}
				else
				{
					return y.ChangeSetID.CompareTo(x.ChangeSetID);
				}
			});
			return histories;
		}

		public static string GetFolderNameUsingServerRootPath(string name)
		{
			int indexOfSlash = name.LastIndexOf("/");
			string folderName = indexOfSlash == -1 ? Constants.ServerRootPath : name.Substring(0, indexOfSlash);
			if (folderName == "$")
				return Constants.ServerRootPath;
			return folderName;
		}

		public static string CombinePath(string path1, string path2)
		{
			if (path1.EndsWith("/"))
			{
				if (path2.StartsWith("/"))
				{
					return path1 + path2.Substring(1);
				}
				return path1 + path2;
			}
			if (path2.StartsWith("/"))
			{
				return path1 + path2;
			}
			return path1 + "/" + path2;
		}

        public static void CooperativeSleep(int millisecondsTimeout)
        {
            // We will NOT use Thread.Sleep(), since that one is strictly blocking.
            // Thread.Join(), however, keeps doing standard COM and SendMessage() pumping,
            // which means that GC of COM objects is improved/possible.
            // See:
            // "C# How to report nonspecific memory usage" http://stackoverflow.com/a/3840190/1222997
            // http://support.microsoft.com/kb/828988
            //   "If you must use STA threads to create COM components,
            //    the STA threads must pump messages regularly.
            //    To pump messages for a short time, call the Thread.Join method, as follows:"
            // http://bytes.com/topic/c-sharp/answers/470374-thread-sleep-vs-thread-join
            // http://bytes.com/topic/c-sharp/answers/484585-contextswitchdeadlock
            Thread.CurrentThread.Join(millisecondsTimeout);
        }

        /// <summary>
        /// Comment-only helper:
        /// may be used to centrally have one single breakpoint configured only
        /// which manages to catch all known cases
        /// which have been deemed to be potentially "interesting".
        /// So either set central breakpoint within this helper,
        /// or set it at various places which you are interested in
        /// which have their code "annotated"/"documented" with this helper invocation;
        /// however, to avoid invocation bloat
        /// (avoid triggering all the time when having a breakpoint here),
        /// it should better only be invoked
        /// for pretty much "unusual", "exceptional" situations.
        ///
        /// For marking sites (/context)
        /// where exceptions are originating from (thrown),
        /// it should either be called directly prior to throwing
        /// (or ideally directly within the exception class's constructor),
        /// or (for cases where exception throw sites are *unreachable*
        /// i.e. in toolkits)
        /// it should be called within our nearest catch() handler.
        /// </summary>
        /// <remarks>
        /// Side note: it should also be very useful
        /// to determine exception throw sites
        /// by enabling exception throw notification
        /// in MSVS Exceptions dialog (Ctrl-Alt-E).
        /// </remarks>
        [Conditional("DEBUG")]
        public static void DebugUsefulBreakpointLocation()
        {
            // DEBUG_SITE: useful breakpoint location.
            // Or possibly also uncomment this:
            //System.Diagnostics.Debugger.Launch();

            // Side note about Debugger.Launch() use in general:
            // while using it
            // might be tempting for situations
            // where one did not have a debugger session open
            // when interesting things happened,
            // for single-process environments (i.e. non-IIS desktop SvnBridge I guess)
            // having the single process stalled at the debugger launch prompt wait
            // will block *all* potential clients of this process,
            // which is something that one might want to avoid...
        }

        /// <summary>
        /// Returns the size (in bytes)
        /// which a cache buffer mechanism
        /// is recommended/advised to maximally have in total,
        /// in order to try to avoid
        /// running into excessive GC issues
        /// (generation promoting, LOH).
        /// </summary>
        /// <remarks>
        /// On 32bit systems, there are severe LOH fragmentation
        /// issues, thus make sure to retain allocations for short
        /// amounts of time only (avoid GC generation "midlife crisis"),
        /// by strongly reducing amount of advance buffering/caching.
        /// </remarks>
        public static long GetCacheBufferTotalSizeRecommendedLimit()
        {
            long limit;

            long BUFFER_SIZE_LIMIT_64BIT = 100000000;
            long BUFFER_SIZE_LIMIT_32BIT = 10000000;

            limit = NeedAvoidLongLivedMemoryFragmentation ? BUFFER_SIZE_LIMIT_32BIT : BUFFER_SIZE_LIMIT_64BIT;

            return limit;
        }

        public static bool NeedAvoidLongLivedMemoryFragmentation
        {
            get
            {
                bool isAddressSpace64bitWide = (8 == IntPtr.Size);
                bool isAddressSpaceSufficientlyLarge = (isAddressSpace64bitWide);
                bool needAvoidLongLivedMemoryFragmentation = !(isAddressSpaceSufficientlyLarge);
                return needAvoidLongLivedMemoryFragmentation;
            }
        }
	}

    /// <summary>
    /// Provide a helper which implicitly supplies the *specific-length*
    /// value (to help the questionably robust GC LOH implementation do its job)
    /// to the base class ctor on 32bit systems
    /// yet leaves 64bit systems unconstrained
    /// (supply standard zero value for dynamic length behaviour).
    /// </summary>
    public sealed class MemoryStreamLOHSanitized : MemoryStream
    {
        // FxCop warning "Do not initialize unnecessarily"
        // but since that assignment is intentionally completely coldpath we don't care.
        private static readonly int capacityCtorParm = (IntPtr.Size == 8) ? 0 : Constants.AllocSize_AvoidLOHCatastrophy;
        public MemoryStreamLOHSanitized()
            : base(capacityCtorParm)
        {
        }
    }
}
