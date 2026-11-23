using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Tekla.PowerFab.Extension;

namespace Hanlon.PowerFab.Nesting
{
    // Signatures are based on Tekla's official External Nesting Workflow Demo.
    // Receives the request and opens the nesting window. Writes occur only after the user chooses Save nest.
    public sealed class Extension : IExtension, INestRequest
    {
        private IExtensionManager? extensionManager;
        public string GetDisplayName()
        {
            return PowerFabNestApi.SourceApplicationName;
        }

        public string GetID()
        {
            return "c81ed122-c823-47b7-a3f1-621cc1ed42a1";
        }

        public void SetExtensionManager(IExtensionManager extensionManager)
        {
            this.extensionManager = extensionManager;
        }

        public bool SendNestRequest(string nestRequestXML, out string errorMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nestRequestXML))
                {
                    errorMessage = "PowerFab supplied an empty nesting request.";
                    return false;
                }

                var readSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                XDocument request;
                using (var textReader = new StringReader(nestRequestXML))
                using (var xmlReader = XmlReader.Create(textReader, readSettings))
                {
                    request = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
                }

                if (request.Root == null)
                {
                    errorMessage = "The nesting request does not contain an XML root element.";
                    return false;
                }

                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(documents))
                {
                    errorMessage = "Windows could not locate your Documents folder.";
                    return false;
                }

                string exportFolder = Path.Combine(documents, "Hanlon", "PowerFab-ABE", "Exports");
                Directory.CreateDirectory(exportFolder);

                var snapshot = NestingSnapshot.FromDocument(request);
                string destination = NestNaming.UniqueRequestPath(exportFolder, NestNaming.ForRequest(snapshot));
                string temporary = destination + ".tmp";

                try
                {
                    var writeSettings = new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        Indent = false,
                        NewLineHandling = NewLineHandling.None
                    };
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = XmlWriter.Create(stream, writeSettings))
                    {
                        // Serializing ensures the XML encoding declaration matches the saved bytes.
                        // Parts, CNC data, inventory and namespaces remain in the document.
                        request.Save(writer);
                    }
                    File.Move(temporary, destination);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        try { File.Delete(temporary); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }

                Func<string, string>? api = extensionManager == null
                    ? null : new Func<string, string>(extensionManager.executeAPIRequest);
                var readModel=extensionManager==null?null:new PowerFabReadModel(extensionManager.createDbCommand);
                if (!NestingWindow.Open(snapshot, destination, api, readModel, out string windowError))
                {
                    errorMessage = "The XML was saved to " + destination
                        + ", but the nesting window could not open: " + windowError;
                    return false;
                }

                errorMessage = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Hanlon nesting request capture failed: " + exception.Message;
                return false;
            }
        }
    }
}
