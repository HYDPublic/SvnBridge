using System.IO;
using System.Text;
using System.Xml;
using SvnBridge.Infrastructure;
using SvnBridge.Interfaces;
using SvnBridge.Protocol;
using SvnBridge.Utility;
using SvnBridge.SourceControl;

namespace SvnBridge.Handlers
{
	public class PropPatchHandler : PropRequestHandlerBase
	{
        protected override void Handle(
            IHttpContext context,
            TFSSourceControlProvider sourceControlProvider,
            StreamWriter output)
		{
			IHttpRequest request = context.Request;
			IHttpResponse response = context.Response;

            string originalXml;
			using (StreamReader sr = new StreamReader(request.InputStream))
			{
                originalXml = sr.ReadToEnd();
			}
            try
            {
                string correctXml = BrokenXml.Escape(originalXml);
                string requestPath = GetPath(request);

                // FIXME: judging from XML namespace strings below,
                // I'm wondering whether some of the places below are missing
                // an extendedNamespaces switch.
                bool extendedNamespaces = false;
                if (correctXml.Contains("http://subversion.tigris.org/xmlns/custom/"))
                    extendedNamespaces = true;

                PropertyUpdateData data = Helper.DeserializeXml<PropertyUpdateData>(correctXml);
                SetResponseSettings(response, "text/xml; charset=\"utf-8\"", Encoding.UTF8, 207);
                // RFC4918 "9.2 PROPPATCH Method" "Responses to this method MUST NOT be cached."
                SetResponseHeader_CacheControl_Uncached(response);

                    PropPatch(sourceControlProvider, data, extendedNamespaces, requestPath, output);
            }
            catch
            {
                // Assigning originalXml content here seems to be asymmetric
                // compared to all other use sites and to what DefaultLogger does with it
                // (SerializeXmlString()), however most likely it is due to the escaping issue above
                // that we don't have a choice...
                OnErrorRetainRequestInfo_RequestBody(originalXml);
                throw;
            }
		}

        private void PropPatch(TFSSourceControlProvider sourceControlProvider, PropertyUpdateData request, bool extendedNamespaces, string requestPath, TextWriter output)
		{
			string activityPlusPath = requestPath.Substring(10);
			if (activityPlusPath.StartsWith("/"))
			{
				activityPlusPath = activityPlusPath.Substring(1);
			}

			string itemPathUndecoded = activityPlusPath.Substring(activityPlusPath.IndexOf('/'));
			string itemPath = Helper.Decode(itemPathUndecoded);
			string activityId = activityPlusPath.Split('/')[0];

            // Internet sez: "Servers MUST process PROPPATCH instructions in document order
            // (an exception to the normal rule that ordering is irrelevant)".
            // Also, for set/remove, some internet logs show an order of D:set then D:remove,
            // thus we likely would want to obey that as well.

            // FIXME: that handling is quite some bullcrap.
            // PROPPATCH should be able to handle
            // *both* set/remove (and in that order!!)
            // **within the same request**.
            // This means:
            // - get rid of this artificially split handling
            // - move handling of svn log property into handling of set property.
            // This should perhaps best be done via some higher-level isolation:
            // First do all incoming requests (with those handlers possibly
            // being branched away generically, judged by different namespaces
            // which are responsible for different properties etc.)
            // and store their result,
            // *then* start generating the XML output given those results.
			if (request.Set.Prop.Properties.Count > 0)
			{
				if (IsPropertyName(request.Set.Prop.Properties[0], "log"))
					OutputLogResponse(requestPath, request, sourceControlProvider, extendedNamespaces, activityId, output);
				else
					OutputSetPropertiesResponse(requestPath, request, sourceControlProvider, activityId, output, itemPath);
			}
			else if (request.Remove.Prop.Properties.Count > 0)
			{
				output.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
				output.Write("<D:multistatus xmlns:D=\"DAV:\" xmlns:ns3=\"http://subversion.tigris.org/xmlns/dav/\" xmlns:ns2=\"http://subversion.tigris.org/xmlns/custom/\" xmlns:ns1=\"http://subversion.tigris.org/xmlns/svn/\" xmlns:ns0=\"DAV:\">\n");
				output.Write("<D:response>\n");
				output.Write("<D:href>" + GetLocalPath("/" + Helper.Encode(requestPath.Substring(0, requestPath.Length-1))) + "</D:href>\n");
				output.Write("<D:propstat>\n");
                output.Write("<D:prop>\n");

				foreach (XmlElement element in request.Remove.Prop.Properties)
				{
                    sourceControlProvider.RemoveProperty(activityId, itemPath, GetPropertyName(element));
					OutputElement(output, element);
                }

                output.Write("</D:prop>\n");
                output.Write("<D:status>HTTP/1.1 200 OK</D:status>\n");
				output.Write("</D:propstat>\n");
				output.Write("</D:response>\n");
				output.Write("</D:multistatus>\n");
			}
		}

		private static string GetPropertyName(XmlElement element)
		{
			string propertyName = BrokenXml.UnEscape(element.LocalName);
			if (element.NamespaceURI == WebDav.Namespaces.TIGRISSVN)
				propertyName = "svn:" + propertyName;
			return propertyName;
		}

		private void OutputElement(TextWriter output, XmlElement element)
		{
            string elementName = BrokenXml.UnEscape(element.LocalName);

			if (element.NamespaceURI == WebDav.Namespaces.SVNDAV)
                output.Write("<ns3:" + elementName + "/>\r\n");
			else if (element.NamespaceURI == WebDav.Namespaces.TIGRISSVN)
                output.Write("<ns1:" + elementName + "/>\r\n");
			else if (element.NamespaceURI == WebDav.Namespaces.DAV)
                output.Write("<ns0:" + elementName + "/>\r\n");
			else //custom
                output.Write("<ns2:" + elementName + "/>\r\n");
		}

        private void OutputSetPropertiesResponse(string requestPath, PropertyUpdateData request, TFSSourceControlProvider sourceControlProvider, string activityId, TextWriter output, string itemPath)
		{
			foreach (XmlElement prop in request.Set.Prop.Properties)
			{
				sourceControlProvider.SetProperty(activityId, itemPath, GetPropertyName(prop), prop.InnerText);
			}
			output.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
			output.Write("<D:multistatus xmlns:D=\"DAV:\" xmlns:ns3=\"http://subversion.tigris.org/xmlns/dav/\" xmlns:ns2=\"http://subversion.tigris.org/xmlns/custom/\" xmlns:ns1=\"http://subversion.tigris.org/xmlns/svn/\" xmlns:ns0=\"DAV:\">\n");
			output.Write("<D:response>\n");
			output.Write("<D:href>" + GetLocalPath("/"+Helper.Encode(requestPath)) + "</D:href>\n");
			output.Write("<D:propstat>\n");
            output.Write("<D:prop>\n");
            foreach (XmlElement element in request.Set.Prop.Properties)
			{
				OutputElement(output, element);
			}
            output.Write("</D:prop>\n");
            output.Write("<D:status>HTTP/1.1 200 OK</D:status>\n");
			output.Write("</D:propstat>\n");
			output.Write("</D:response>\n");
			output.Write("</D:multistatus>\n");
		}

        private void OutputLogResponse(string requestPath, PropertyUpdateData request, TFSSourceControlProvider sourceControlProvider, bool extendedNamespaces, string activityId, TextWriter output)
		{
			sourceControlProvider.SetActivityComment(activityId, request.Set.Prop.Properties[0].InnerText);
			output.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            if (extendedNamespaces)
                output.Write("<D:multistatus xmlns:D=\"DAV:\" xmlns:ns3=\"http://subversion.tigris.org/xmlns/dav/\" xmlns:ns2=\"http://subversion.tigris.org/xmlns/custom/\" xmlns:ns1=\"http://subversion.tigris.org/xmlns/svn/\" xmlns:ns0=\"DAV:\">\n");
            else
                output.Write("<D:multistatus xmlns:D=\"DAV:\" xmlns:ns1=\"http://subversion.tigris.org/xmlns/svn/\" xmlns:ns0=\"DAV:\">\n");

			output.Write("<D:response>\n");
			output.Write("<D:href>" + GetLocalPath("/"+Helper.Encode(requestPath)) + "</D:href>\n");
			output.Write("<D:propstat>\n");
			output.Write("<D:prop>\n");
			output.Write("<ns1:log/>\r\n");
			output.Write("</D:prop>\n");
			output.Write("<D:status>HTTP/1.1 200 OK</D:status>\n");
			output.Write("</D:propstat>\n");
			output.Write("</D:response>\n");
			output.Write("</D:multistatus>\n");
		}

        private static bool DetermineSkipOKResults(IHttpRequest request)
        {
            return DetermineBriefOutput(request);
        }
	}
}
