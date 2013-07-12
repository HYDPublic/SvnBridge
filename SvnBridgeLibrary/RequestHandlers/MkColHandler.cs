using System.Text;
using System.Text.RegularExpressions;
using SvnBridge.Exceptions;
using SvnBridge.Interfaces;
using SvnBridge.Utility;
using SvnBridge.SourceControl;

namespace SvnBridge.Handlers
{
    public class MkColHandler : RequestHandlerBase
    {
        protected override void Handle(
            IHttpContext context,
            TFSSourceControlProvider sourceControlProvider)
        {
            IHttpRequest request = context.Request;
            IHttpResponse response = context.Response;

            string requestPath = GetPath(request);
            string itemPath = Helper.Decode(requestPath);

            try
            {
                MakeCollection(itemPath, sourceControlProvider);

                SendCreatedResponse(request, response, itemPath, request.Url.Host, request.Url.Port.ToString());
            }
            catch (FolderAlreadyExistsException)
            {
                SendFailureResponse(response, itemPath, request.Url.Host, request.Url.Port.ToString());
            }
        }

        private static void MakeCollection(string itemPath, TFSSourceControlProvider sourceControlProvider)
        {
            if (!itemPath.StartsWith("//"))
            {
                itemPath = "/" + itemPath;
            }

            Match match = Regex.Match(itemPath, @"//!svn/wrk/([a-zA-Z0-9\-]+)/?");
            string folderPath = itemPath.Substring(match.Groups[0].Value.Length - 1);
            string activityId = match.Groups[1].Value;
            sourceControlProvider.MakeCollection(activityId, Helper.Decode(folderPath));
        }

        private static void SendCreatedResponse(IHttpRequest request, IHttpResponse response, string itemPath, string server, string port)
        {
            SetResponseSettings(response, "text/html", Encoding.UTF8, 201);

            response.AppendHeader("Location", "http://" + request.Headers["Host"] + "/" + itemPath);

            string responseContent = GetResourceCreatedResponse(
                WebDAVResourceType.Collection,
                itemPath,
                server,
                port);

            WriteToResponse(response, responseContent);
        }

        private static void SendFailureResponse(IHttpResponse response, string itemPath, string server, string port)
        {
            SetResponseSettings(response, "text/html; charset=iso-8859-1", Encoding.UTF8, 405);

            response.AppendHeader("Allow", "TRACE");

            string responseContent =
                "<!DOCTYPE HTML PUBLIC \"-//IETF//DTD HTML 2.0//EN\">\n" +
                "<html><head>\n" +
                "<title>405 Method Not Allowed</title>\n" +
                "</head><body>\n" +
                "<h1>Method Not Allowed</h1>\n" +
                "<p>The requested method MKCOL is not allowed for the URL /" + itemPath + ".</p>\n" +
                "<hr>\n" +
                "<address>Apache/2.0.59 (Win32) SVN/1.4.2 DAV/2 Server at " + server + " Port " + port + "</address>\n" +
                "</body></html>\n";

            WriteToResponse(response, responseContent);
        }
    }
}
