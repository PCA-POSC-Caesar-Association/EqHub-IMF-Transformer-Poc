
using System.Text;
using System.Text.Json;
using VDS.RDF;
using VDS.RDF.Writing;

namespace Transformer;

public static class JsonToTTLTransformer
{
    public static Uri imfBase = new Uri("http://ns.imfid.org/imf#");
    public static string Transform(string json)
    {
        var classMappings = GetMappings("Mapping/classMapping.ttl");
        var propertyMappings = GetMappings("Mapping/propertyMapping.ttl");

        // Digging around in JSON for the parts we need.
        var jsonObject = JsonDocument.Parse(json);
        //Class definition
        var requirementsClassElement = jsonObject.RootElement
            .GetProperty("data")
            .GetProperty("data")
            .GetProperty("requirementsClass");
        var shacl = FetchShaclClassDefinition(classMappings, requirementsClassElement);
        // Hack to make the SHACL graph point to the draft environment
        shacl = shacl.Replace("https://posccaesar.org", "https://draft.posccaesar.org");
        // Properties
        var properties = jsonObject.RootElement
            .GetProperty("data")
            .GetProperty("properties")
            .EnumerateArray()
            .ToList();
        // Equipment - not sure if this is a good name
        var equipment = jsonObject.RootElement
            .GetProperty("data")
            .GetProperty("data");

        var equipmentGraph = CreateEquipmentTTLRepresentation(shacl, properties, propertyMappings, equipment);

        // This is needed makework due to the way dotnetrdf expects you to only serialize a graph to a stream or a file
        // For this purpose passing strings back and forth makes it easier to read the code
        using MemoryStream outputStream = new();
        equipmentGraph.SaveToStream(new StreamWriter(outputStream, new UTF8Encoding(false)), new CompressingTurtleWriter());

        var graphString = Encoding.UTF8.GetString(outputStream.ToArray());
        return graphString;
    }

    private static Graph CreateEquipmentTTLRepresentation(string shacl, List<JsonElement> properties, List<Mapping> propertyMappings, JsonElement equipment)
    {
        var graph = new Graph();
        var eqhubProductId = equipment.GetProperty("eqhubProductId").GetInt64().ToString();
        if (string.IsNullOrEmpty(eqhubProductId))
        {
            throw new ArgumentException("The JSON does not contain a valid 'eqhubProductId' property.");
        }
        var equipmentUri = new Uri($"https://example.org/equipment/{eqhubProductId}");
        var equipmentNode = graph.CreateUriNode(equipmentUri);

        var shaclGraph = new Graph();
        shaclGraph.LoadFromString(shacl);
        var shaclSubject = shaclGraph.GetTriplesWithPredicateObject(
            shaclGraph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type")),
            shaclGraph.CreateUriNode(new Uri("http://ns.imfid.org/imf#BlockType"))
        ).Select(t => t.Subject).FirstOrDefault();

        graph.Assert(new Triple(equipmentNode, shaclGraph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type")), shaclSubject));
        // Find all the properties that are described in the SHACL graph
        foreach (var triple in shaclGraph.Triples)
        {
            if (triple.Predicate.ToString().EndsWith("property") && triple.Object is INode propertyNode)
            {
                var pathTriple = shaclGraph.GetTriplesWithSubjectPredicate(propertyNode, shaclGraph.CreateUriNode(new Uri("http://www.w3.org/ns/shacl#path"))).FirstOrDefault();
                var hasValueTriple = shaclGraph.GetTriplesWithSubjectPredicate(propertyNode, shaclGraph.CreateUriNode(new Uri("http://www.w3.org/ns/shacl#hasValue"))).FirstOrDefault();

                if (pathTriple != null && hasValueTriple != null)
                {
                    var pathNode = pathTriple.Object;
                    var hasValueNode = hasValueTriple.Object;
                    graph.Assert(new Triple(equipmentNode, pathNode, hasValueNode));
                }
            }
        }

        var rdfTypeNode = graph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"));

        foreach (var property in properties)
        {
            var propertyId = property.GetProperty("propertyRequirement").GetProperty("propertyRequirementId").GetString();
            var dataProperty = property.GetProperty("data");
            var propertyValue = dataProperty.EnumerateObject()
                .Where(p => p.Name.EndsWith("value", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.GetString())
                .FirstOrDefault() ?? string.Empty;


            var propertyMapping = propertyMappings.FirstOrDefault(m => m.Source == propertyId);
            if (propertyMapping == null)
            {
                continue; // Skip unmapped properties
            }

            var propertyNode = graph.CreateUriNode(propertyMapping.Target);
            var valueNode = graph.CreateLiteralNode(propertyValue);
            graph.Assert(new Triple(equipmentNode, propertyNode, valueNode));
        }
        return graph;
    }

    public static string FetchShaclClassDefinition(List<Mapping> classMappings, JsonElement requirementsClassElement)
    {
        if (!requirementsClassElement.TryGetProperty("requirementsClassId", out var classIdElement))
        {
            throw new ArgumentException("The JSON does not contain a 'requirementsClassId' property.");
        }

        var classId = classIdElement.GetString();
        if (classId == null)
        {
            throw new ArgumentException("'requirementsClassId' property is null or invalid.");
        }

        // Find the corresponding URI in the classMappings
        var classMapping = classMappings.FirstOrDefault(m => m.Source == classId);
        if (classMapping == null)
        {
            throw new KeyNotFoundException($"No mapping found for class ID: {classId}");
        }
        var httpClient = new HttpClient();
        return httpClient.GetStringAsync(classMapping.Target).Result.ToString();
    }


    public static List<Mapping> GetMappings(string fileName)
    {
        var mappings = new List<Mapping>();
        var graph = new VDS.RDF.Graph();
        VDS.RDF.Parsing.FileLoader.Load(graph, fileName);

        foreach (var triple in graph.Triples)
        {
            if (triple.Subject is VDS.RDF.UriNode subjectNode &&
                triple.Predicate is VDS.RDF.UriNode predicateNode &&
                triple.Object is VDS.RDF.UriNode objectNode)
            {
                mappings.Add(new Mapping
                {
                    Source = subjectNode.Uri.ToString().Replace("https://draft.posccaesar.org/eqhub/v0.0.0.41/Id/", ""),
                    Target = objectNode.Uri
                });
            }
        }

        return mappings;
    }

    public class Mapping
    {
        public required string Source { get; set; }
        public required Uri Target { get; set; }
    }
}
