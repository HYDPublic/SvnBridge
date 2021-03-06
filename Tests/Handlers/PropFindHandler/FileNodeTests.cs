using System.Xml;
using SvnBridge.Interfaces;
using SvnBridge.PathParsing;
using Xunit;
using SvnBridge.Infrastructure;
using SvnBridge.Nodes;
using SvnBridge.SourceControl;
using Tests;
using Attach;
using SvnBridge.Handlers;

namespace UnitTests
{
    public class FileNodeTests : HandlerTestsBase
    {
        [Fact]
        public void VerifyBaselineRelativePathPropertyGetsEncoded()
        {
            XmlDocument xml = new XmlDocument();
            ItemMetaData item = new ItemMetaData();
            item.Name = "A !@#$%^&()_-+={[}];',~`..txt";
            FileNode node = new FileNode(item, null);

            string result = node.GetProperty(new GetHandler(false), "baseline-relative-path");

            Assert.Equal(
                "<lp2:baseline-relative-path>A !@#$%^&amp;()_-+={[}];',~`..txt</lp2:baseline-relative-path>", result);
        }

        [Fact]
        public void GetProperty_CheckedIn_GetsEncodedProperly()
        {
            TFSSourceControlProvider sourceControlProvider = stubs.CreateTFSSourceControlProviderStub();

            XmlDocument xml = new XmlDocument();
            ItemMetaData item = new ItemMetaData();
            item.ItemRevision = 5700;
            item.Name = "A !@#$%^&()_-+={[}];',~`..txt";
            FileNode node = new FileNode(item, null);

            GetHandler handler = new GetHandler(false);
			handler.Initialize(context, new PathParserSingleServerWithProjectInPath(tfsUrl));
            handler.SetSourceControlProvider(sourceControlProvider);
        	string result = node.GetProperty(handler, "checked-in");

            Assert.Equal(
                "<lp1:checked-in><D:href>/!svn/ver/5700/A%20!@%23$%25%5E&amp;()_-+=%7B%5B%7D%5D%3B',~%60..txt</D:href></lp1:checked-in>",
                result);
        }

        [Fact]
        public void GetProperty_CheckedIn_ReturnsCorrectResult()
        {
            TFSSourceControlProvider sourceControlProvider = stubs.CreateTFSSourceControlProviderStub();

            XmlDocument xml = new XmlDocument();
            ItemMetaData item = new ItemMetaData();
            item.ItemRevision = 5700;
            item.Name = "Test.txt";
            FileNode node = new FileNode(item, null);

            GetHandler handler = new GetHandler(false);
            handler.Initialize(context, new PathParserSingleServerWithProjectInPath(tfsUrl));
            handler.SetSourceControlProvider(sourceControlProvider);
            string result = node.GetProperty(handler, "checked-in");

            Assert.Equal("<lp1:checked-in><D:href>/!svn/ver/5700/Test.txt</D:href></lp1:checked-in>", result);
        }
    }
}