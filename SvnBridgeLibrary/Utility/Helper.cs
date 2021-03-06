using System; // IntPtr.Size , Random
using System.Diagnostics; // Conditional
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using CodePlex.TfsLibrary.ObjectModel;
using SvnBridge.Net;

namespace SvnBridge.Utility
{
    using SvnBridge.Infrastructure; // Container

	public static class Helper
	{
		private static readonly byte[] _emptyBuffer = new byte[0];

    // For information on URI encoding (e.g. D:href element),
    // see http://tools.ietf.org/html/rfc3986 .
    // Please also see
    // "HTTP Extensions for Web Distributed Authoring and Versioning (WebDAV)"
    //   http://tools.ietf.org/html/rfc4918#section-8.3.1
    //
    // Percent-encoding as upper-case: RFC3986 notes that percent-encoded strings
    // should be normalized to *upper-case* values (capitalize currently done
    // programmatically below, but it might be done wrongly for URIs
    // and perhaps should have a static coding set for upper-case things).
    // Also, it notes "In addition to the case normalization issue noted above,
    // some URI producers percent-encode octets that do not
    // require percent-encoding, resulting in URIs that
    // are equivalent to their non-encoded counterparts.
    // These URIs should be normalized by decoding any percent-encoded octet
    // that corresponds to an unreserved character"
    // Note that percent-encoding is something different
    // from Punycode-encoding as used by IRI (RFC3987).

    // Note that in SvnBridge client case, a /tfsserver:8080/ string part
    // is *NOT* a RFC3986 ABNF "authority" (i.e., hostname) URI part -
    // it's the initial part of the "hier-part" (i.e. "path-absolute")
    // part, with potentially different encoding requirements
    // (when used for D:href element etc.).

    // Intermingled transcoding: I really *don't* think that it's correct
    // to have a coding map for percent-encoding
    // which then suddenly contains one grossly foreign XML entity escaping, too.
    // Quite likely that's due to intermingling transcoding requirements
    // of two *unrelated* transcoding-requiring layers rather than doing separate precise, correct
    // transcoding steps each, from each layer with its precise transcoding requirements
    // to another layer with likely *other* distinct requirements,
    // with an entire roundtrip ending up correct.
    // Seems like this originated from the ampersand mentioning at
    // http://tools.ietf.org/html/rfc4918#section-8.3.1, however it's
    // still not a good idea (which is very obvious when looking at
    // the dirty '&' special-casing in Encode())
    // to intermingle **separate** (read: to be applied in series!!)
    // URI encoding and XML entity escaping concerns.

    // So, e.g. for a fully internationalized resource (umlauts etc.),
    // an *encoding* process chain would be:
    // original string (UTF-8)
    // --> RFC3986 encoding run
    // --> Non-RFC3986 (Non-ASCII) parts to hex-encoded representation encoding run
    // --> XML entity escaping run
    // [-----> on the HTTP protocol wire!]
    // And the decoding chain would then be applied precisely in reverse.
    // Watch out for double-transcoding issues with '%' char, though
    // (doing RFC3986 encoding *prior* to encoding of left-over non-ASCII chars
    // probably is crucial).

    // We'll try to correct wrong transcoding issues as we encounter them,
    // but we might temporarily hit problems though, namely if we announce content in our
    // *encoding* which then gets fed back by the client in other SVN requests
    // where we do NOT do *decoding* via the same (hopefully properly corrected) transformation yet.

    // TODO: get rid of all public APIs
    // which are horribly unspecifically named "Encode"/"Decode" -
    // naming should be *specific*,
    // to indicate exactly which requirements are being met.
    // And when adding an encoding type description part,
    // then prefer something like "PercentEncode"
    // rather than "Encode" or "Escape".

    // See e.g. http://www.websitedev.de/temp/rfc3986-check.html.gz
    // for a very nice RFC3986 online checker.
    //
    // [1] http://stackoverflow.com/questions/602642/server-urlencode-vs-httputility-urlencode

    // The existing maps seem buggy for purposes of RFC3986 URI transform,
    // i.e. for payload content (potentially containing URI-special chars)
    // which is being announced via URIs (i.e., D:href etc.), as specified by current RFC3986
    // (hmm, or would these mechanisms be specifically written for obsolete RFC2396 only?
    // The thing that's relevant here is probably what assumptions WebDAV is making...).
    // We definitely need to transform all "reserved" codes for
    // any payload content which should not conflict with URI-side
    // delimiters, plus the percent-encoding-required '%' char, obviously.
    // reserved = gen-delims / sub-delims
    // gen-delims  = ":" / "/" / "?" / "#" / "[" / "]" / "@"
    // sub-delims  = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="

		private static readonly string[] DECODED = new string[] { "%",   "#",   " ",   "^",   "{",   "[",   "}",   "]",   ";",   "`",   "&" };
		private static readonly string[] ENCODED = new string[] { "%25", "%23", "%20", "%5e", "%7b", "%5b", "%7d", "%5d", "%3b", "%60", "&amp;" };

		private static readonly string[] DECODED_B = new string[] { "&",     "<",    ">" };
		private static readonly string[] ENCODED_B = new string[] { "&amp;", "&lt;", "&gt;" };

        // XML has 5 (FIVE) mandatory entities to be encoded, not merely the 3 above.
        // Possibly _B is thus buggy, but this should be decided on a case-by-case basis,
        // thus provide another helper for the corrected(?) encoding.
	// (note that it turned out that e.g. D:comment does not seem to require all of these escapes after all).
	// See also http://stackoverflow.com/questions/1664208/encode-quotes-in-html-body
		private static readonly string[] DECODED_B_FIXED = new string[] { "&",     "<",    ">",    "\"",     "'" };
		private static readonly string[] ENCODED_B_FIXED = new string[] { "&amp;", "&lt;", "&gt;", "&quot;", "&apos;" };

		private static readonly string[] DECODED_C = new string[] { "%",   "#",   " ",   "^",   "{",   "[",   "}",   "]",   ";",   "`" };
		private static readonly string[] ENCODED_C = new string[] { "%25", "%23", "%20", "%5e", "%7b", "%5b", "%7d", "%5d", "%3b", "%60" };

        /// <summary>
        /// Helper for fast, central appending/joining/combining of two arrays.
        /// Hopefully a lot better/faster than
        /// and thus preferable to
        /// using a manual (read: icache-bloating) .Add() loop.
        /// However, for repeated appending of arrays,
        /// it might still be better
        /// to do a repeated .AddRange() on an existing List
        /// (memory management ought to be more predictable).
        /// Method specially crafted to be able to easily cope
        /// with both either first or second arg null.
        /// </summary>
        /// <remarks>
        /// "C#: Merging,Appending, Extending two arrays in .NET (csharp, mono)"
        ///    https://gist.github.com/lsauer/7919764
        /// Alternative might be Buffer.BlockCopy()
        /// (see http://www.dotnetperls.com/combine-arrays ),
        /// but I doubt that it's better.
        /// </remarks>
        public static T[] ArrayCombine<T>(T[] arrayInitial, T[] arrayToBeAppended)
        {
            int arrayInitialLength = (null != arrayInitial) ? arrayInitial.Length : 0;
            int arrayToBeAppendedLength = (null != arrayToBeAppended) ? arrayToBeAppended.Length : 0;
            T[] combined = new T[arrayInitialLength + arrayToBeAppendedLength];
            int idxWritePos = 0;
            if (null != arrayInitial)
            {
                arrayInitial.CopyTo(combined, idxWritePos);
                idxWritePos += arrayInitialLength;
            }
            if (null != arrayToBeAppended)
            {
                arrayToBeAppended.CopyTo(combined, idxWritePos);
            }
            return combined;
        }

        public static void AppendToStream(MemoryStream stream, byte[] data)
        {
            ArraySegment<byte> arrSeg = new ArraySegment<byte>(data, 0, data.Length);
            AppendToStream(stream, arrSeg);
        }

        /// <summary>
        /// Will append a certain number of data bytes
        /// (as passed in in a flexible/args-optimized manner
        /// via usual ArraySegment helper)
        /// to the *end* of a *resizeable* MemoryStream
        /// in a controlled manner, i.e.:
        /// - preserving current (i.e. restoring original) stream position (i.e. it will NOT have changed .Position after Write()!)
        /// - resize (enlarge) stream if needed (avoid .Write() out-of-range ArgumentException)
        /// </summary>
        public static void AppendToStream(MemoryStream stream, ArraySegment<byte> arrSeg)
        {
            var streamLengthOrig = stream.Length;
            var positionWriteStart = streamLengthOrig;
            WriteToStreamYetKeepPosUnchanged(stream, positionWriteStart, arrSeg);
        }

        public static void WriteToStreamYetKeepPosUnchanged(MemoryStream stream, long positionWriteStart, ArraySegment<byte> arrSeg)
        {
            var numToBeWritten = arrSeg.Count;
            var requiredMinimumCapacity = Math.Max(positionWriteStart + numToBeWritten, stream.Length);
            StreamEnsureCapacity(stream, requiredMinimumCapacity);

            var positionBackup = stream.Position;
            try
            {
                // http://stackoverflow.com/a/7239011
                //stream.Seek(0, SeekOrigin.End);
                stream.Position = positionWriteStart;
                // MSDN MemoryStream.Write():
                // "If the write operation is successful,
                // the current position within the stream
                // is advanced by the number of bytes written."
                stream.Write(arrSeg.Array, arrSeg.Offset, numToBeWritten);
            }
            finally
            {
                stream.Position = positionBackup;
            }
        }

        /// <remarks>
        /// Certain inner C# handlers are said to be called "EnsureCapacity",
        /// so let's use compatible naming.
        /// </remarks>
        public static void StreamEnsureCapacity(MemoryStream stream, long requiredMinimumCapacity)
        {
            // Hmm, MSDN MemoryStream.Write() says:
            // "Except for a MemoryStream constructed with a byte[] parameter,
            // write operations at the end of a MemoryStream expand the MemoryStream." -
            // but what exactly does that mean?? I'm rather sure
            // that I did get out-of-range ArgumentException:s
            // for *non*-fixed-array MemoryStream
            // when trying to Write() beyond existing .Capacity...
            // So I'm quite sure that I do need this handling in here.
            // Maximum Confusion Galore...
            bool needEnlargeCapacity = (stream.Capacity < requiredMinimumCapacity);
            if (needEnlargeCapacity)
            {
                // Important comment, to avoid the following issue:
                // AWFUL Capacity handling!! (GC catastrophy)
                // .Capacity should most definitely *NEVER* be directly (manually) modified,
                // since framework ought to know best
                // how to increment .Capacity in suitably future-proof-sized steps,
                // rather than restricted to this-time needs
                // (read: it's NOT useful
                // to keep incrementing [read: reallocating!!]
                // a continuously aggregated perhaps 8MB .Capacity
                // by some perhaps 4273 Bytes each!)
                //
                // --> use .SetLength() API
                // since it internally calculates (whenever needed)
                // the next suitable .Capacity value.
                var lengthBackup = stream.Length;
                stream.SetLength(requiredMinimumCapacity);
                stream.SetLength(lengthBackup); // ...yet for now keep .Length compatible with prior value
            }
        }

        /// <summary>
        /// Copy entire input stream content to output stream.
        /// Does NOT manually do an implicit (layer-violating) Flush()
        /// (needs to be done by user where desired).
        /// </summary>
        /// <remarks>
        /// http://stackoverflow.com/questions/10664458/memorystream-writetostream-destinationstream-versus-stream-copytostream-desti
        /// "Stream to Stream Copy"
        ///   http://computer-programming-forum.com/4-csharp/1ea2ff52ad65efc1.htm
        /// http://codesnippets.fesslersoft.de/copy-one-stream-to-another/
        /// http://bytes.com/topic/asp-net/answers/309519-how-save-stream-data-file-using-filestream
        /// "When is GetBuffer() on MemoryStream ever useful?"
        ///   http://stackoverflow.com/a/13477396
        /// "MemoryStream.WriteTo(Stream destinationStream) versus
        /// Stream.CopyTo(Stream destinationStream)"
        ///   http://stackoverflow.com/a/10665486
        /// While of course we aren't able to do a zero-copy mechanism
        /// (c.f. Linux network driver infrastructure)
        /// in cases where one stream *needs* to be transferred to another,
        /// we should still try to achieve maximally efficient handling.
        ///
        /// While there is MemoryStream.WriteTo()
        /// which could be used in case of a MemoryStream type,
        /// this would need casting,
        /// thus it's probably better
        /// to keep one generic open-coded variant
        /// for all Stream types.
        ///
        /// I strongly suspect
        /// that we do not want to implement this transfer
        /// via a (inefficient?)
        /// BinaryReader/Writer abstraction layer:
        /// http://stackoverflow.com/a/23696270
        /// </remarks>
        public static void StreamCopy(
            Stream source,
            Stream target)
        {
            int BUFFER_SIZE = StreamCopyBufferSize();

            byte[] buffer = new byte[BUFFER_SIZE];

            int count;
            try
            {
                for (;;)
                {
                    count = source.Read(buffer, 0, BUFFER_SIZE /* ==(!) buffer.Length */);
                    bool haveData = (0 != count);
                    if (!(haveData))
                    {
                        break;
                    }

                    target.Write(buffer, 0, count);
                }
            }
            finally
            {
                //target.Flush(); // when used: definitely within an exception-finally{}!!
            }
        }

        /// <remarks>
        /// http://stackoverflow.com/questions/1540658/net-asynchronous-stream-read-write
        /// </remarks>
        public static void StreamCopyViaAsync(
            Stream source,
            Stream target)
        {
            int BUFFER_SIZE = StreamCopyBufferSize();

            byte[] readbuffer = new byte[BUFFER_SIZE];
            byte[] writebuffer = new byte[BUFFER_SIZE];
            IAsyncResult asyncResult = null;

            // This should work down to ~0.01kb/sec
            int minSpeedBytesPerSec = 16; // Prefer a nicely dividable (modulo!) value
            var writeTimeoutSec = CalculateWriteTimeoutForStreamCopyViaAsync(writebuffer.Length, minSpeedBytesPerSec);
            var writeTimeoutMS = 1000 * writeTimeoutSec;
            for (; ; )
            {
                // Read data into the readbuffer.  The previous call to BeginWrite, if any,
                //  is executing in the background..
                int read = source.Read(readbuffer, 0, readbuffer.Length);

                // Ok, we have read some data and we're ready to write it, so wait here
                //  to make sure that the previous write is done before we write again.
                if (asyncResult != null)
                {
                    asyncResult.AsyncWaitHandle.WaitOne(writeTimeoutMS);
                    target.EndWrite(asyncResult); // Last step to the 'write'.
                    bool isCompleted = asyncResult.IsCompleted;
                    if (!(isCompleted)) // Make sure the write really completed.
                    {
                        throw new IOException("Stream write failed.");
                    }
                }

                bool haveReadData = (0 < read);
                if (!(haveReadData))
                {
                    return;
                }

                // Swap the read and write buffers so we can write what we read,
                // and we can then use the other buffer for our next read.
                byte[] tbuf = writebuffer;
                writebuffer = readbuffer;
                readbuffer = tbuf;

                // Asynchronously write the data, asyncResult.AsyncWaitHandle will
                // be set when done.
                asyncResult = target.BeginWrite(writebuffer, 0, read, null, null);
            }
        }

        private static int CalculateWriteTimeoutForStreamCopyViaAsync(int numToBeWritten, int minSpeedBytesPerSec)
        {
            return (numToBeWritten / minSpeedBytesPerSec);
        }

        /// <summary>
        /// Chooses a suitable size for temporary buffers
        /// used for copy transfers.
        /// </summary>
        /// <remarks>
        /// http://stackoverflow.com/questions/10664458/memorystream-writetostream-destinationstream-versus-stream-copytostream-desti#comment13837116_10665486
        /// Should choose a size which is sufficiently large (efficient copy),
        /// but avoid very excessive size:
        /// - stay below LOH threshold
        /// - may lose memory non-pressure benefits
        ///   of a fully incrementally streamy operation
        /// </remarks>
        private static int StreamCopyBufferSize()
        {
            return 64 * 1024;
        }

        /// <summary>
        /// Clean encapsulation helper
        /// to return an array *copy* of the precise range
        /// described by ArraySegment (within its original array).
        /// To avoid such possibly superfluous copying
        /// (i.e., ideally achieve zero-copy operation),
        /// in many cases you should probably decide
        /// to instead pass the original ArraySegment
        /// between layers/methods.
        /// </summary>
        /// <remarks>
        /// http://stackoverflow.com/questions/19868007/return-part-of-array
        /// </remarks>
        public static T[] ArraySegmentToArray<T>(ArraySegment<T> arrSeg)
        {
            var length = arrSeg.Count;
            T[] realArray = new T[length];
            Array.Copy(arrSeg.Array, arrSeg.Offset, realArray, 0, length);
            return realArray;
        }

        /// <remarks>
        /// WARNING: Q&amp;D implementation!
        /// Does not even care about .Position or other stuff!
        /// </remarks>
        public static bool StreamCompare(
            Stream s1,
            Stream s2)
        {
            bool isMatch = true;

            byte[] d1 = new byte[4096];
            byte[] d2 = new byte[4096];

            for (; ; )
            {
                var c1 = s1.Read(d1, 0, d1.Length);
                var c2 = s2.Read(d2, 0, d2.Length);

                bool isMatchingReadCount = (c1 == c2);
                if (!(isMatchingReadCount))
                {
                    isMatch = false;
                    break;
                }

                bool haveMoreData = (0 != c1);
                if (!(haveMoreData))
                {
                    break;
                }

                isMatch = ArrayCompare(
                    d1,
                    d2,
                    c1);
            }

            return isMatch;
        }

        public static bool ArrayCompare(byte[] a1, byte[] a2, int count)
        {
            bool isMatch = true;

            for (int i = 0; i < count; ++i)
            {
                var d1 = a1[i];
                var d2 = a2[i];
                if (d1 != d2)
                {
                    isMatch = false;
                    break;
                }
            }

            return isMatch;
        }

        /// <remarks>
        /// http://stackoverflow.com/questions/472906/converting-a-string-to-byte-array-without-using-an-encoding-byte-by-byte
        /// Note that output will usually end up as 16bit values!!
        /// </remarks>
        public static byte[] GetBytes(
            string str)
        {
            byte[] bytes = new byte[str.Length * sizeof(char)];
            System.Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <remarks>
        /// Important related usage side note:
        /// E.g. for classes derived from TextWriter
        /// (StreamWriter, StringWriter)
        /// It seems to be much preferable
        /// to be doing multiple
        ///     output.Write(string1);
        ///     output.Write(string2);
        ///     output.Write(string3);
        /// rather than
        ///     output.Write(string1 + string2 + string3);
        /// i.e. having string data appending / concatenation efforts
        /// done internally in appropriate/optimized/*central* toolkit layers
        /// should be generally preferred to doing
        /// potentially allocation-prone / GC-taxing
        /// manual (user side) string concatenation efforts.
        /// MSDN KB306822:
        /// "
        /// * If you are in an environment that supports streaming the
        ///   data, such as in an ASPX Web Form or your application is
        ///   writing the data to disk, consider avoiding the buffer
        ///   overhead of concatenation or the StringBuilder, and write the
        ///   data directly to the stream through the Response.Write method
        ///   or the appropriate method for the stream in question.
        ///
        /// * Try to reuse the existing StringBuilder class rather than
        ///   reallocate each time you need one. This limits the growth of
        ///   the heap and reduces garbage collection. In either case, using
        ///   the StringBuilder class makes more efficient use of the heap
        ///   than using the + operator.
        /// "
        ///
        /// Related very important/detailed StringWriter counter-points:
        /// - http://stackoverflow.com/a/11048978
        ///   ( http://stackoverflow.com/questions/602279/stringwriter-or-stringbuilder )
        /// - http://stackoverflow.com/a/2980953
        ///   ( http://stackoverflow.com/questions/2980805/string-assembly-by-stringbuilder-vs-stringwriter-and-printwriter )
        /// </remarks>
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

        /// <summary>
        /// Sanitizing / commenting encapsulation helper.
        /// Returns StreamWriter.BaseStream,
        /// while carefully handling the very special restriction / "feature"
        /// that writing to .BaseStream may end up in underlying output
        /// *prior* to (actually cached) StreamWriter.Write()-dispatched data!!!
        /// This very, very special issue of .BaseStream access
        /// again is NOT MSDN-documented AFAICS!!!!!
        /// </summary>
        public static Stream AccessStreamWriterBaseStreamSanitized(StreamWriter sw)
        {
            sw.Flush(); // !!!!
            return sw.BaseStream;
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
            // This XmlReader impl, as opposed to all others,
            // had a "using" statement, however I really don't think that this is needed here,
            // since there shouldn't be any unmanaged resources involved
            // and it's configured to NOT close the underlying stream anyway (InitializeNewXmlReaderSettings()).
			XmlReader reader = XmlReader.Create(requestStream, InitializeNewXmlReaderSettings());
			return DeserializeXml<T>(reader);
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
			return sw.ToString();
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
				// I believe that we do want to use
				// properly generic full IRegistrationService handling here, too,
				// rather than doing dirt-ugly open-coding
				// of version-specific URLs.
				// That way (by doing this everywhere in our implementation)
				// we'll have a fighting chance of surviving
				// some future product version (to be specific: web service protocol) upgrades
				// (as long as the specific interfaces that we make use of
				// did remain unchanged/compatible indeed).
				// And while having to go through a full IRegistrationService processing
				// ought to be more overhead (less performance)
				// than a direct open-coded query,
				// improved compatibility is way more important.
				string urlTfsService;
				// For the simple purpose of checking whether a web service exists,
				// using an unsafe credential ought to be ok
				// (actual use should then be properly using a client-provided credential).
				ICredentials credentials = GetUnsafeNetworkCredential();
				string output_expected;
				bool useRegistrationService = true;
				if (useRegistrationService)
				{
					// Hmm, NOPE - IRegistrationService seems to be the service
					// which is *sitting* at (being provided by) that location,
					// i.e. it understandably then does not allow to *query* that location itself
					// (its service list does not contain that location).
					// Thus, it seems
					// for a valid TFS existence check
					// we ought to resort
					// to an actual TFS service to be queried,
					// and then probably best choose the one
					// which is *most* common/basic.
					//string serviceType = "Services";
					//string interfaceName = "Registration";
					// output_expected = "Team Foundation Registration web service";
					string serviceType = "VersionControl";
					string interfaceName = "ISCCProvider";
					// Reasons for choosing this validation string
					// within the resulting web page content:
					// - don't check against "comment" / "user guidance" parts (they may be very volatile)
					// - don't check against result URL based on serviceType / interfaceName
					//   (since this is the very thing that we would want to abstract away)
					// - so, check for a sufficiently specific interface API name
					//   which we know to be actually using
					output_expected = "QueryItemsExtended";
					IRegistrationService registration = Container.Resolve<IRegistrationService>();
					urlTfsService = registration.GetServiceInterfaceUrl(
						url,
						credentials,
						serviceType,
						interfaceName);
				}
				else
				{
					urlTfsService = url + "/Services/v1.0/Registration.asmx";
					output_expected = "Team Foundation Registration web service";
				}
				Uri uriTfsService = new Uri(urlTfsService);
				WebRequest request = WebRequest.Create(uriTfsService);
				request.Credentials = credentials;
				request.Proxy = CreateProxy(proxyInformation);
				request.Timeout = 60000;

				using (WebResponse response = request.GetResponse())
				using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
				{
					string output = reader.ReadToEnd();
					bool isExpectedService = (output.Contains(output_expected));
					return isExpectedService;
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
            int bufLen = buffer.Length;
            do
            {
                num = data.Read(buffer, 0, bufLen);
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
            StringBuilder sb = new StringBuilder(hash.Length * HexByteStringLength);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            // Performance: .ToLower() operation logically actually is per-hexbyte, but... :))
            return sb.ToString().ToLower();
        }

        // (Almost) generic encoder (transform item set #2 to item set #1)
		private static string Encode(string[] encoded, string[] decoded, string value, bool capitalize)
		{
			if (value == null)
			{
				return value;
			}

            int decodedLen = decoded.Length;
			for (int i = 0; i < decodedLen; i++)
			{
                // Due to ampersand special case probably cannot do .ToUpper() globally at end, right?
				if (capitalize && !(decoded[i].Equals("&")))
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

        // (Almost) generic decoder (transform item set #1 to item set #2)
        private static string Decode(string[] encoded, string[] decoded, string value, bool capitalize)
		{
			if (value == null)
			{
				return value;
			}

			for (int i = encoded.Length - 1; i >= 0; i--)
			{
				if (capitalize)
				{
					value = value.Replace(encoded[i].ToUpper(), decoded[i]);
				}
				else
				{
					value = value.Replace(encoded[i], decoded[i]);
				}
			}

			return value;
		}

        private static string Decode(string value, bool capitalize)
        {
            return Decode(ENCODED, DECODED, DecodeURIComponent_NonASCII(value), capitalize);
        }

        // FIXME: several functions here use capitalize == false,
        // but this conflicts with RFC3986 recommendation to use upper-case for consistency!
        // So this should probably be changed,
        // except for cases where lower-case happens to be a foreign-party communication requirement.
		public static string Encode(string value)
		{
			return EncodeURIComponent_NonASCII(Encode(value, false));
		}

		public static string Encode(string value, bool capitalize)
		{
			return EncodeURIComponent_NonASCII(Encode(ENCODED, DECODED, value, capitalize));
		}

		public static string Decode(string value)
		{
			return Decode(value, false);
		}

		public static string EncodeB(string value)
		{
			return Encode(ENCODED_B, DECODED_B, value, false);
		}

        public static string EncodeB_fixed(string value)
        {
            return Encode(ENCODED_B_FIXED, DECODED_B_FIXED, value, false);
        }

		public static string DecodeB(string value)
		{
			return Decode(value, false);
		}

        // Preferred newer API.
        public static string EncodeURIComponent_RFC3986_plus_i18n(string value_utf8)
        {
            return EncodeURIComponent_NonASCII(Encode(ENCODED_C, DECODED_C, value_utf8, true));
        }

        // Preferred newer API.
        public static string DecodeURIComponent_RFC3986_plus_i18n(string value_utf8)
        {
            return Decode(DecodeURIComponent_NonASCII(value_utf8), true);
        }

		// Deprecated. Capitalizing form of Encode().
		public static string EncodeC(string value)
		{
			return EncodeURIComponent_RFC3986_plus_i18n(value);
		}

		// Deprecated. Capitalizing decode.
		public static string DecodeC(string value)
		{
			return DecodeURIComponent_RFC3986_plus_i18n(value);
		}

    /// <summary>
    /// Comment-only directly-HttpUtility.UrlEncode()-wrapping function.
    /// </summary>
    /// <remarks>
    /// Note that UrlEncode() is problematic
    /// since it will replace ' ' by '+'
    /// (UrlPathEncode() does a more common replacement: ' ' by "%20" -
    ///   but MSDN UrlPathEncode() says "Do not use").
    /// </remarks>
    private static string MyUrlEncode(string href_utf8)
    {
        return HttpUtility.UrlEncode(href_utf8);
    }

    /// <summary>
    /// This function seems B0RKEN and should thus be deprecated. See comments below.
    /// </summary>
		public static string UrlEncodeIfNecessary(string href_utf8)
		{
                        // Hmm... why can't we use the venerable
                        // Uri.EscapeDataString() method instead?
                        // Surely would be much faster, too...
			StringBuilder sb = new StringBuilder(href_utf8.Length * PercentEnhancedHexByteStringLength);
			foreach (char c in href_utf8)
			{
        // I don't know WHAT THE H*LL this function does:
        // A) no comment anywhere
        // B) Non-ASCII codepages (well, single-byte ones) are 8bit i.e.
        //    resulting in a 0..*255* range! Why "> 256"??
        // C) why 25X? Pure ASCII is 7bit! (making the gross assumption that this function
        //    was originally intended to be used to escape some chars in non-ASCII range)
        // D) Finally, it is emphatically *NOT* fully complete URI input which
        //    an encoder should process - rather, it's *payload* segments which should
        //    be encoded *prior* to forming full URI syntax from it,
        //    in order to prevent accidentally matching parts
        //    from being mistaken as URI parts ('/' etc.)
        //    --> misnomer.
        // C.f. the report at [1]
				if (c > 256)
				{
					sb.Append(MyUrlEncode(c.ToString()));
				}
				else
				{
					sb.Append(c);
				}
			}
			return sb.ToString();
		}

    private static bool CharIsInASCIIRange(char c)
    {
        return (c <= 127);
    }

    private static bool CharIsInSingleByteRange(char c)
    {
        return (c <= 255);
    }

    /// <remarks>
    /// RFC3986 covers URI components in ASCII range only,
    /// i.e. it does not cover International Domain Names (IDN).
    /// Thus, if you want to ensure that an URI remains within RFC3986
    /// compatibility requirements (ASCII-only),
    /// *all* incompatible chars (i.e. Non-ASCII range, too) need to be percent-encoded.
    ///
    /// We'll strictly limit this transcoding layer
    /// to transcoding percent-encoded characters of non-ASCII range only!
    /// Reasoning: RFC3986 handlers are expected to (and thus should always be used to)
    /// properly handle all other cases of percent-encoded chars.
    ///
    /// While it's sometimes recommended to use Uri.EscapeDataString() rather
    /// than the seemingly somewhat buggy HttpUtility.UrlPathEncode()
    /// (see [1], e.g. handling of '%' sign),
    /// Uri.EscapeDataString() provides optional(!) IRI functionality
    /// which is beyond the scope of RFC3986 (this seems to also mean that it
    /// uses Punycode rather than percent-encoding!).
    /// Thus we have to fall back to using buggy(?) HttpUtility.UrlPathEncode()
    /// (WARNING: it assumes string to be in proper UTF-8 encoding,
    /// thus you ought to pass compatible input!)
    /// Nope, better: we'll simply use Uri.HexEscape() for this task!
    /// </remarks>
    private static string EncodeURIComponent_NonASCII(string href_utf8)
    {
			StringBuilder sb = new StringBuilder(href_utf8.Length * PercentEnhancedHexByteStringLength);
			foreach (char c in href_utf8)
			{
				bool isInASCIIRange = (CharIsInASCIIRange(c));
				bool notNeedPercentEncoding = (isInASCIIRange);
				if (notNeedPercentEncoding)
				{
					sb.Append(c);
				}
				else
				{
					string escaped;
					//bool isBeyondSingleByteRange = (!CharIsInSingleByteRange(c));
					//bool canUseApi_UriHexEscape = (!isBeyondSingleByteRange);
                    // We need proper UTF-8 multi-byte sequences
                    // (at least in most[?] areas!),
                    // *not* simple percent-encoding of up-to-255 values!
                    bool canUseApi_UriHexEscape = false;
					if (canUseApi_UriHexEscape)
					{
						// Uri.HexEscape() supports single-byte values (0..255) only,
						// else throws ArgumentOutOfRangeException!
						// (e.g.: Euro sign [0x20ac])
						// I could have implemented non-single-byte handling
						// via a fallback (after catching Uri.HexEscape() exceptions),
						// but I decided against doing it this way
						// since for certain URL content
						// non-standard characters are *not* really exceptional
						// i.e. this should *not* be handled via "exceptional" cases.
						escaped = Uri.HexEscape(c);
					}
					else
					{
						escaped = HttpUtility.UrlEncode(c.ToString());
					}
					sb.Append(escaped);
				}
			}
			return sb.ToString();
    }

    private static string DecodeURIComponent_NonASCII(string href_utf8)
    {
        bool containsPercentSign = (href_utf8.Contains("%"));
        bool likelyNeedsPercentDecoding = (containsPercentSign); // very important shortcut :)
        return likelyNeedsPercentDecoding ? DecodeURIComponent_NonASCII_Do(href_utf8) : href_utf8;
    }

    /// <remarks>
    /// Design rationale:
    /// We'll try to keep as much handling as possible
    /// in standard "string" (internally UTF16, but that's not relevant) domain.
    /// Then only for those parts which need "special" handling
    /// we'll collect UTF8 bytes
    /// and then once this multi byte sequence is complete
    /// we'll append the conversion result to the string.
    /// </remarks>
    private static string DecodeURIComponent_NonASCII_Do(string href_utf8)
    {
        string result; // debug helper var

        var href_utf8Len = href_utf8.Length;
        var sbInitialCapacity = href_utf8Len;
        StringBuilder sb = new StringBuilder(sbInitialCapacity);
        List<byte> utf8Bytes = new List<byte>();
        for (int index = 0; index < href_utf8Len; /* specially conditionally incremented below */)
        {
            // XXX: this handling is the complementary part to Uri.HexEscape(),
            // which turned out to be restricted to 0..255 char value range.
            // Thus it may very well be
            // that we need to fix this handling here, too.
            bool isPercentEncodedChar = Uri.IsHexEncoding(href_utf8, index);
            if (isPercentEncodedChar)
            {
                int index_new = index;
                char c_candidate = Uri.HexUnescape(href_utf8, ref index_new);
                if (CharIsInASCIIRange(c_candidate))
                {
                    // *pass on* RFC3986-side ASCII arguments unprocessed!
                    int encoded_arg_length = index_new - index;
                    string percent_encoded_ascii_char = href_utf8.Substring(index, encoded_arg_length);
                    sb.Append(percent_encoded_ascii_char);
                }
                else
                {
                    utf8Bytes.Add((byte)c_candidate);
                }
                index = index_new;
            }
            else
            {
                AppendUTF8BytesIfAvailable(ref sb, utf8Bytes);

                sb.Append(href_utf8[index]);
                ++index;
            }
        }
        AppendUTF8BytesIfAvailable(ref sb, utf8Bytes); // to support case of having PE:d elements *only*

        result = sb.ToString();

        return result;
    }

    private static void AppendUTF8BytesIfAvailable(
        ref StringBuilder sb,
        List<byte> utf8Bytes)
    {
        bool haveData = (0 < utf8Bytes.Count);
        bool nothingToAdd = !(haveData);
        if (!(nothingToAdd))
        {
            string fromUTF8 = Encoding.UTF8.GetString(utf8Bytes.ToArray());
            sb.Append(fromUTF8);
            utf8Bytes.Clear();
        }
    }

    /// <summary>
    /// Helper to work around strongly suspected deficiencies
    /// in API Uri.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped):
    /// it does NOT "SafeUnescape" *may*-be-encoded (but not *must*-be-encoded!!!) chars such as '$',
    /// which is a real PITA.
    /// </summary>
    /// <param name="uriComponent">URI component</param>
    /// <param name="charsToPercentEncode">the list of chars to be PE:d (or all if null)</param>
    /// <param name="charsToNotPercentEncode">the list of chars to NOT be PE:d</param>
    /// <returns></returns>
    public static string PercentEncodeConditional(string uriPayload, string charsToPercentEncode, string charsToNotPercentEncode)
    {
        StringBuilder sb = new StringBuilder(uriPayload.Length * PercentEnhancedHexByteStringLength);
        foreach (char c in uriPayload)
        {
            bool needPercentEncodeThisChar = false;

            string strC = c.ToString();
            bool isPercentEncodeLimitedToSubset = (charsToPercentEncode != null);
            if (isPercentEncodeLimitedToSubset)
            {
                if (charsToPercentEncode.Contains(strC))
                {
                    needPercentEncodeThisChar = true;
                }
            }
            else
            {
                needPercentEncodeThisChar = true;
            }
            if (needPercentEncodeThisChar)
            {
                if (charsToNotPercentEncode != null)
                {
                    if (charsToNotPercentEncode.Contains(strC))
                    {
                        needPercentEncodeThisChar = false;
                    }
                }
            }
            if (needPercentEncodeThisChar)
            {
                sb.Append(HttpUtility.UrlEncode(strC));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

        private static int HexByteStringLength
        {
            get { return 2; }
        }

        private static int PercentEnhancedHexByteStringLength
        {
            get { return 3; }
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

			if (updatingForwardInTime)
			{
			    histories.Sort(delegate(SourceItemHistory x, SourceItemHistory y)
			    {
					return x.ChangeSetID.CompareTo(y.ChangeSetID);
				});
            }
            else
			{
			    histories.Sort(delegate(SourceItemHistory x, SourceItemHistory y)
				{
					return y.ChangeSetID.CompareTo(x.ChangeSetID);
				});
			}
			return histories;
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

        /// <summary>
        /// Detects the case where input strings are exactly equal for a
        /// case-insensitive compare yet non-equal for a subsequent case-sensitive one,
        /// as e.g. in the case of same-length strings "root/MyPath" vs. "root/MyPATH",
        /// *without* bogusly indicating a mismatch due to simply having different-length strings.
        /// </summary>
        public static bool IsStringsPreciseCaseSensitivityMismatch(string arg1, string arg2)
        {
            bool isPreciseMismatch = false;
            // We better don't do an initial shortcut via Length mismatch compare,
            // since then we might end up with a case strings are in fact "equal"
            // according to locale-specific expectations, yet do have slightly different length.

            bool isCaseInsensitiveMatch = string.Equals(arg1, arg2, StringComparison.InvariantCultureIgnoreCase);
            if (isCaseInsensitiveMatch)
            {
                bool isCaseSensitiveMatch = string.Equals(arg1, arg2, StringComparison.InvariantCulture);
                isPreciseMismatch = !(isCaseSensitiveMatch);
            }
            return isPreciseMismatch;
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

        /// <remarks>
        /// Could instead be doing bit-based next-increment alignment via
        /// new = (required+(align+1) &amp; ~(align-1);
        /// but this likely is only correct for 2^n-based align values.
        /// </remarks>
        public static int ValueAlignNext(
            int oldLength,
            int requiredLength,
            int alignLength)
        {
            return ValueAlignNextImplDiv(
                oldLength,
                requiredLength,
                alignLength);
        }

        /// <remarks>
        /// ~ O(1) complexity.
        /// </remarks>
        private static int ValueAlignNextImplDiv(
            int oldLength,
            int requiredLength,
            int alignLength)
        {
            int newLength;

            int numSegs = (requiredLength / alignLength) + 1;
            newLength = alignLength * numSegs;

            return newLength;
        }

        /// <remarks>
        /// Annoying complexity of ~ O(n) due to loop.
        /// </remarks>
        private static int ValueAlignNextImplWhile(
            int oldLength,
            int requiredLength,
            int alignLength)
        {
            int newLength;

            newLength = 0;
            while (newLength < requiredLength)
            {
                newLength += alignLength;
            }

            return newLength;
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

    public class DebugRandomActivatorImplRandom
    {
        // random member done via delay-init.
        // Not choosing static member (due to thread-safety requirement, yet Random
        // very understandably not making any such guarantees).
        // See also http://stackoverflow.com/questions/4933823/class-system-random-why-not-static
        private Random random /* = null*/;

        private Random Random
        {
            get
            {
                // Delay-construct random member (first use only)
                if (null == random)
                {
                    random = new Random();
                }
                return random;
            }
        }

        public bool TryGetTrue(int percentageForSuccessResult)
        {
            bool success = (Random.Next(0, 99) < percentageForSuccessResult);

            return success;
        }
    }

    public class DebugRandomActivatorImplDummyAlwaysFalse
    {
        public bool TryGetTrue(int percentageForSuccessResult)
        {
            return false;
        }
    }

    /// <summary>
    /// Helper class to deliver a success result
    /// in a certain percentage of invocations only
    /// (debug-only!! Else result always false).
    /// May e.g. be used to optionally do
    /// some special expensive verification/comparison runs.
    /// </summary>
    /// <remarks>
    /// Should be used in very rare and justified cases only,
    /// since it will obviously disrupt
    /// the application's sufficiently(?) deterministic behaviour.
    /// Hmm, since this will also affect unit tests
    /// (which suddenly face going into code paths
    /// which the unit test stub environment
    /// does not have preparations for)
    /// we will keep it disabled.
    /// </remarks>
//#if DEBUG
#if false
    public sealed class DebugRandomActivator : DebugRandomActivatorImplRandom
#else
    public sealed class DebugRandomActivator : DebugRandomActivatorImplDummyAlwaysFalse
#endif
    {
        public bool YieldTrueOnPercentageOfCalls(int percentageForSuccessResult)
        {
            return TryGetTrue(percentageForSuccessResult);
        }
    }
}
