using Xunit;
using Transformer;
using System.Text.Json;
using VDS.RDF;
using VDS.RDF.Shacl;
using VDS.RDF.Writing;
using VDS.RDF.Shacl.Validation;
using System.Text;

namespace Tests;

public class TransformerTests
{
    [Fact]
    public void TestMethod()
    {
        var testDataFolder = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "TestData");
        var files = Directory.GetFiles(testDataFolder, "*.json");
        var classMappings = JsonToTTLTransformer.GetMappings("Mapping/classMapping.ttl");

        foreach (var file in files)
        {

            var json = File.ReadAllText(file);
            var jsonObject = JsonDocument.Parse(json);
            var requirementsClassElement = jsonObject.RootElement
                .GetProperty("data")
                .GetProperty("data")
                .GetProperty("requirementsClass");
            var shacl = JsonToTTLTransformer.FetchShaclClassDefinition(classMappings, requirementsClassElement);
            shacl = shacl.Replace("https://posccaesar.org", "https://draft.posccaesar.org");

            var result = JsonToTTLTransformer.Transform(json);

            var graph = new Graph();
            graph.LoadFromString(result);
            graph.SaveToFile(file + ".ttl", new CompressingTurtleWriter());

            var shaclGraph = new Graph();
            shaclGraph.LoadFromString(shacl);

            var validator = new ShapesGraph(shaclGraph);
            Report report = validator.Validate(graph);

            using MemoryStream outputStream = new MemoryStream();
            report.Normalised.SaveToStream(new StreamWriter(outputStream, new UTF8Encoding(false)), new CompressingTurtleWriter());

            var graphString = Encoding.UTF8.GetString(outputStream.ToArray());

            // Assert.False(report.Conforms, $"The graph does not conform to the SHACL shape. {graphString}");
        }
    }
}
