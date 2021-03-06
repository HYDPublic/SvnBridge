using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using SvnBridge.Utility; // Helper

namespace SvnBridge.Net
{
    /// <summary>
    /// I don't know... somehow this class doesn't have proper separation of concerns
    /// (which led to all sorts of issues).
    /// Rather than implementing both chunked _and_ non-chunked operation,
    /// it should be specific class instantiations (either chunked or non-chunked) in advance,
    /// depending on response.SendChunked.
    /// And _then_ one could implement it in a way to form a new chunk per each Flush().
    /// Perhaps similar to what Acme.Serve.servlet.http.ChunkedOutputStream does.
    ///
    /// Important explanations:
    /// In chunked mode, actively doing any flushing handling is ok,
    /// whereas in non-chunked mode we *cannot* start writing partial data
    /// (i.e. MemoryStream needs to keep collecting until the very end)
    /// since the Content-Length: header is a *fixed* value
    /// and thus can be written at the very end of content creation only
    /// (when final Length is known).
    /// </summary>
    /// Side note: XXX I'm unsure whether layering of
    /// (especially) ListenerResponseStream vs. ListenerResponse
    /// is fully correct
    /// (e.g. I'm strongly wondering
    /// whether writing of specific HTTP headers
    /// in a ListenerResponseStream stream object is ok...)
    public class ListenerResponseStream : Stream
    {
        protected bool flushed /* = false */;
        protected bool headerWritten /* = false */;
        protected readonly ListenerResponse response;
        protected readonly Stream stream;
        protected MemoryStream streamBuffer;
        protected static readonly byte[] chunkFooterChunk = Encoding.UTF8.GetBytes("\r\n");
        protected static readonly byte[] chunkFooterFinalZeroChunk = Encoding.UTF8.GetBytes("0\r\n\r\n");

        public ListenerResponseStream(ListenerResponse response,
                                      Stream stream)
        {
            this.response = response;
            this.stream = stream;

            this.streamBuffer = CreateMemoryStream();
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        /// <remarks>
        /// Since non-chunked operation may write a full content body *once* only,
        /// it would likely be a better idea to move writeout from Flush()
        /// (someone might call Flush() multiple times, and the first one would happen with
        /// partial data only!!) to the final Close().
        /// UNFORTUNATELY in some cases (Listener.cs FlushConnection())
        /// Close() will not be called on shutdown (why!?!?),
        /// thus we have to resort to sending the buffer here for now.
        /// UPDATE: hopefully handling is more suitable now...
        /// </remarks>
        public override void Flush()
        {
            bool skipFlush = IsInterimFlushProhibited;
            if (!(skipFlush))
            {
                FlushDo();
            }
        }

        /// <summary>
        /// Comment-only helper.
        /// </summary>
        /// <remarks>
        /// Due to complex handling here
        /// (class handles both header parts *and* content part!),
        /// in case of non-chunked operation
        /// we cannot allow interim Flush()
        /// since that would risk writing headers
        /// which would indicate Content-Length:
        /// as length of *currently partially finished* length
        /// (in case of a very early Flush()
        /// this would even lead to zero Content-Length:!).
        /// </remarks>
        private bool IsInterimFlushProhibited
        {
            get
            {
                bool isInterimFlushProhibited;

                bool isPlainHugeBlobData = !(response.SendChunked);
                bool needWholeDataForHeaderInfo = (isPlainHugeBlobData);
                isInterimFlushProhibited = (needWholeDataForHeaderInfo);

                return isInterimFlushProhibited;
            }
        }

        /// <remarks>
        /// NOTE: this method could (and did!) get called _multiple_ times,
        /// thus it should better be made
        /// to not have single-invocation-only constraints.
        /// </remarks>
        private void FlushDo()
        {
            if (!flushed)
            {
                if (response.SendChunked)
                {
                    // currently we don't do much for chunked mode here,
                    // other than ensuring proper header writeout.
                    WriteHeaderIfNotAlreadyWritten();
                }
                else
                {
                    PushNonChunkedBuffer();
                }

                flushed = true;
            }
            // No need to Flush() the underlying stream member (network stream) here
            // (since its write handling should implicitly know already when to flush),
            // especially since that might end up actively blocking here.
        }

        public override void Close()
        {
            FlushDo(); // may write header! (written *FIRST*!)

            if (response.SendChunked)
            {
                flushed = false;
                stream.Write(chunkFooterFinalZeroChunk, 0, chunkFooterFinalZeroChunk.Length);
                FlushDo(); // ...and a second flush!
            }
            // FIXME: hmm... should we Close() our wrapped Stream member here, too!?
            // Most likely not... (some subsequent output handling might take place).
            // UPDATE: I'm dead certain that we ought to Close() it,
            // since we are within our Stream.Close()
            // where all (the entire chain of) stream dependees
            // is also supposed to be Close()d!!
            // http://stackoverflow.com/questions/13043706/will-streamwriter-flush-also-call-filestream-flush/13043763#13043763
            // NOPE, since this class is directly serving a NetworkStream (response.OutputStream),
            // it seemingly (and, at least in that aspect, correctly)
            // was explicitly designed to *NOT* Close() NetworkStream,
            // in order to have a workaround against
            // very problematic lifetime handling behaviour
            // of pre-4.5 StreamWriter
            // (now there's finally a bool leaveOpen).
            // http://stackoverflow.com/questions/2666888/is-there-any-way-to-close-a-streamwriter-without-closing-its-basestream
            //stream.Close(); <--- DO NOT RE-ACTIVATE THIS!!!!

            base.Close();
        }

        private void PushNonChunkedBuffer()
        {
            WriteHeaderIfNotAlreadyWritten();
            ForwardStreamBuffer();
        }

        private void ForwardStreamBuffer()
        {
            //byte[] buffer = streamBuffer.ToArray(); // *copy* of *used* internal container segment
            //stream.Write(buffer, 0, buffer.Length);
            //stream.Write(streamBuffer.GetBuffer() /* *non-copy* of full internal container length */, 0, (int)streamBuffer.Length);
            streamBuffer.WriteTo(stream);
            // At this point in time, streamBuffer is terre brulee
            // (non-chunked mode, i.e. *fixed* Content-Length:,
            // thus since we already indicated length
            // [of only the content part *currently* residing in stream!]
            // in the header right before,
            // it's over-and-out). Thus:
            // Make sure to re-create (cleanly/fully discard all old content,
            // by discarding old object!):
            streamBuffer = CreateMemoryStream();
        }

        public override int Read(byte[] buffer,
                                 int offset,
                                 int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset,
                                  SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer,
                                   int offset,
                                   int count)
        {
            if (response.SendChunked)
            {
                WriteHeaderIfNotAlreadyWritten();

                byte[] chunkHeader = Encoding.UTF8.GetBytes(string.Format("{0:x}", count) + "\r\n");

                stream.Write(chunkHeader, 0, chunkHeader.Length);
                stream.Write(buffer, offset, count);
                stream.Write(chunkFooterChunk, 0, chunkFooterChunk.Length);
            }
            else
            {
                streamBuffer.Write(buffer, offset, count);
                // No streamBuffer.Flush() here!! (keep collecting all data until content-complete!)
            }
        }

        private static MemoryStream CreateMemoryStream()
        {
            return new Utility.MemoryStreamLOHSanitized();
        }

        private static string GetStatusCodeDescription(int httpStatusCode)
        {
            string statusCodeDescription;

            switch (httpStatusCode)
            {
                case 204:
                    statusCodeDescription = "No Content";
                    break;
                case 207:
                    statusCodeDescription = "Multi-Status";
                    break;
                case 301:
                    statusCodeDescription = "Moved Permanently";
                    break;
                case 401:
                    statusCodeDescription = "Authorization Required";
                    break;
                case 404:
                    statusCodeDescription = "Not Found";
                    break;
                case 405:
                    statusCodeDescription = "Method Not Allowed";
                    break;
                case 500:
                    statusCodeDescription = "Internal Server Error";
                    break;
                case 501:
                    statusCodeDescription = "Method Not Implemented";
                    break;
                default:
                    statusCodeDescription = ((HttpStatusCode)httpStatusCode).ToString();
                    break;
            }

            return statusCodeDescription;
        }

        /// <remarks>
        /// See also
        /// http://stackoverflow.com/questions/2595460/how-can-i-set-transfer-encoding-to-chunked-explicitly-or-implicitly-in-an-asp#comment2744849_2711405
        /// </remarks>
        protected void WriteHeaderIfNotAlreadyWritten()
        {
            if (!headerWritten)
            {
                DoWriteHeader();

                headerWritten = true;
            }
        }

        private void DoWriteHeader()
        {
            string statusCodeDescription = GetStatusCodeDescription(response.StatusCode);

            // Use ctor variant for implicit (*internal*) StringBuilder:
            StringWriter output = new StringWriter();

            output.WriteLine("HTTP/1.1 {0} {1}", response.StatusCode, statusCodeDescription);

            output.WriteLine("Server: " + Constants.SVNServerIdentificationString);

            List<KeyValuePair<string, string>> headers = response.Headers;

            string xPadHeader = null;
            string connection = null;

            foreach (KeyValuePair<string, string> header in headers)
            {
                if (header.Key.Equals("X-Pad"))
                {
                    xPadHeader = header.Value;
                    continue;
                }
                else if (header.Key.Equals("Connection"))
                {
                    connection = header.Value;
                    continue;
                }
                else
                {
                    output.WriteLine("{0}: {1}", header.Key, header.Value);
                }
            }

            if (connection != null)
            {
                output.WriteLine("Connection: {0}", connection);
            }

            if (NeedDateHeader)
            {
                output.WriteLine("Date: {0}", GetDateHeaderValue());
            }

            bool haveEntity = true;
            if (haveEntity)
            {
                if (response.SendChunked)
                {
                    output.WriteLine("Transfer-Encoding: chunked");
                }

                WritePerEachEntityHeaders(
                    output);
            }

            if (!String.IsNullOrEmpty(xPadHeader))
            {
                output.WriteLine("X-Pad: {0}", xPadHeader);
            }

            output.WriteLine("");

            string headersString = output.ToString(); // debug convenience
            byte[] bufferBytes = Encoding.UTF8.GetBytes(headersString);

            stream.Write(bufferBytes, 0, bufferBytes.Length);
        }

        /// <summary>
        /// Comment-only helper.
        /// </summary>
        /// <remarks>
        /// rfc2616 "14.18 Date":
        /// "
        /// The HTTP-date sent in a Date header SHOULD NOT represent a
        /// date and time subsequent to the generation of the message. It
        /// SHOULD represent the best available approximation of the date
        /// and time of message generation, unless the implementation has
        /// no means of generating a reasonably accurate date and time. In
        /// theory, the date ought to represent the moment just before the
        /// entity is generated. In practice, the date can be generated at
        /// any time during the message origination without affecting its
        /// semantic value.
        /// "
        /// </remarks>
        private static bool NeedDateHeader
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Returns Date: header value
        /// as required to be compliant with RFC1123.
        /// "3.3.1 Full Date"
        ///   http://www.w3.org/Protocols/rfc2616/rfc2616-sec3.html
        /// </summary>
        private static string GetDateHeaderValue()
        {
            return DateTime.UtcNow.ToString("R");
        }

        /// <remarks>
        /// http://www.w3.org/Protocols/rfc2616/rfc2616-sec7.html
        /// IMPORTANT NOTE: rfc2518 e.g. "13.4 getcontentlength Property"
        /// seems to strongly suggest
        /// that we actually ought to try to achieve
        /// directly generating various entity-related headers
        /// by doing blindingly simple queries
        /// on the PROPFIND-supplied INode-based objects.
        /// IOW, pseudo code:
        /// if (null != myINode)
        /// {
        ///     WritePerEachEntityHeaders(
        ///         output,
        ///         myINode);
        /// }
        ///
        /// Note that I kept order of Content-Length / Content-Type generation
        /// since many, many unit tests check against that
        /// (and not sure in general
        /// whether there is a required or even recommended order).
        /// </remarks>
        private void WritePerEachEntityHeaders(
            TextWriter output)
        {
            if (!response.SendChunked)
            {
                output.WriteLine("Content-Length: {0}", streamBuffer.Length);
            }

            output.WriteLine("Content-Type: {0}", response.ContentType);
        }
    }
}
