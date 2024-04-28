using System;
using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Newtonsoft.Json;
using Azure.AI.OpenAI;
using Azure;
using System.Net;
using System.ComponentModel;
using Container = Microsoft.Azure.Cosmos.Container;
using System.Configuration;
using Microsoft.Extensions.Configuration;

namespace vectortests
{
    class Program
    {

        //create a CosmosClient instance
        private static CosmosClient cosmosClient;
        private static OpenAIClient openAiClient;

        private static string embeddingsDeployment = "embeddings";

        static async Task Main(string[] args)
        {

            //Get values from appsettings.json file
            IConfiguration Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
                .Build();
            string openAiEndpoint = Configuration["OpenAi:Endpoint"];
            string openAiKey = Configuration["OpenAi:Key"];
            //string cosmosEndpoint = Configuration["CosmosDB:Endpoint"];
            //string cosmosKey = Configuration["CosmosDB:Key"];

            string cosmosEndpoint = Configuration["CosmosDBServerless:Endpoint"];
            string cosmosKey = Configuration["CosmosDBServerless:Key"];


            openAiClient = new(
                endpoint: new Uri(openAiEndpoint),
                keyCredential: new AzureKeyCredential(openAiKey));

            cosmosClient = new CosmosClient(
                accountEndpoint: cosmosEndpoint,
                authKeyOrResourceToken: cosmosKey);

            Container catalogContainer = await CreateCatalogContainer();
            //await LoadCatalogData(catalogContainer);
            //await QueryCatalogData(catalogContainer);

            Container chatContainer = await CreateChatDatabaseContainer();
            string cacheResonse = await QueryCache(chatContainer, 0.95);



        }

        static async Task<Container> CreateCatalogContainer()
        {

            // Create a new database and container
            Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync("catalogdb");

            // Define new container properties including the vector indexing policy
            ContainerProperties properties = new ContainerProperties(id: "catalog", partitionKeyPath: "/Type")
            {
                // Define the vector embedding container policy
                VectorEmbeddingPolicy = new(
                new Collection<Embedding>(
                [
                    new Embedding()
                    {
                        Path = "/Embeddings",
                        DataType = VectorDataType.Float32,
                        DistanceFunction = DistanceFunction.Cosine,
                        Dimensions = 1536
                    }
                ])),
                IndexingPolicy = new IndexingPolicy()
                {
                    // Define the vector index policy
                    VectorIndexes = new()
                    {
                        new VectorIndexPath()
                        {
                            Path = "/Embeddings",
                            Type = VectorIndexType.QuantizedFlat
                        }
                    }
                }
            };

            //Throughput for the container
            ThroughputProperties throughput = ThroughputProperties.CreateAutoscaleThroughput(4000);

            // Create the container
            Container container = await database.CreateContainerIfNotExistsAsync(properties, throughput);

            return container;

        }

        static async Task<Container> CreateChatDatabaseContainer()
        {
            // Create a new database and container
            Database database = cosmosClient.GetDatabase("chatdatabase");

            // Define new container properties including the vector indexing policy
            ContainerProperties properties = new ContainerProperties(id: "chatcontainer", partitionKeyPath: "/sessionId")
            {
                // Define the vector embedding container policy
                VectorEmbeddingPolicy = new(
                new Collection<Embedding>(
                [
                    new Embedding()
                        {
                            Path = "/vectors",
                            DataType = VectorDataType.Float32,
                            DistanceFunction = DistanceFunction.Cosine,
                            Dimensions = 1536
                        }
                ])),
                IndexingPolicy = new IndexingPolicy()
                {
                    // Define the vector index policy
                    VectorIndexes = new()
                        {
                            new VectorIndexPath()
                            {
                                Path = "/vectors",
                                Type = VectorIndexType.QuantizedFlat
                            }
                        }
                }
            };

            // Create the container
            Container container = await database.CreateContainerIfNotExistsAsync(properties);

            return container;
        }

        static async Task<string> QueryCache(Container cacheContainer, double similarityScore)
        {
            string input = "What is the largest lake in North America?";

            float[] vectors = await GetEmbeddingsAsync(input);

            string queryText = "SELECT Top 1 c.UserPrompt, c.CompletionText, x.SimilarityScore FROM(SELECT c.UserPrompt, c.CompletionText, VectorDistance(c.vectors, @vectors, false) as SimilarityScore FROM c) x WHERE x.SimilarityScore > @similarityScore ORDER BY x.SimilarityScore desc";

            var queryDef = new QueryDefinition(
                 query: queryText)
                .WithParameter("@vectors", vectors)
                .WithParameter("@similarityScore", similarityScore);

            using FeedIterator<dynamic> resultSet = cacheContainer.GetItemQueryIterator<dynamic>(queryDefinition: queryDef);

            string cacheResponse = "";

            while (resultSet.HasMoreResults)
            {
                FeedResponse<dynamic> response = await resultSet.ReadNextAsync();

                foreach (var item in response)
                {
                    cacheResponse = item.CompletionText;
                }
            }

            return cacheResponse;

        }

        static async Task LoadCatalogData(Container container)
        {
            // Read the JSON data from the file
            var readstream = new System.IO.StreamReader("./data/catalog.json");
            var json = await readstream.ReadToEndAsync();


            // Deserialize the JSON data into a list of var objects.
            var items = JsonConvert.DeserializeObject<List<dynamic>>(json);

            // Add the items to the container
            foreach (var item in items)
            {
                // Copy the Id field to the id field and make a string
                item["id"] = item["Id"].ToString();
                await container.CreateItemAsync(item);
            }
        }

        static async Task LoadArxivData(Container container)
        {
            //read and load arxiv data in hdf5 file format



        }

        static async Task QueryCatalogData(Container container)
        {

            string input1 = "What can I get to go snowboarding?";
            string input2 = "What can I get to go skiing?";
            string input3 = "What can I get to go hiking?";

            float[] embedding = await GetEmbeddingsAsync(input1);

            string queryText = "SELECT c.Type, c.Brand, c.Name, VectorDistance(c.Embedding, @embedding, false) as SimilarityScore FROM catalog c ORDER BY VectorDistance(c.Embedding, @embedding, false) DESC OFFSET 0 LIMIT 10";
            string queryText2 = "SELECT c.Type, c.Brand, c.Name, VectorDistance(c.Embedding, @embedding, false) as SimilarityScore FROM catalog c ORDER BY VectorDistance(c.Embedding, @embedding, false) DESC";
            string queryText3 = "SELECT Top 1 c.Id, c.Type, c.Brand, c.Name, VectorDistance(c.Embedding, @embedding, false) as SimilarityScore, c.Embedding FROM catalog c ORDER BY VectorDistance(c.Embedding, @embedding, false) DESC";

            var queryDef = new QueryDefinition(
                query: queryText3)
                .WithParameter("@embedding", embedding);

            using FeedIterator<dynamic> resultSet = container.GetItemQueryIterator<dynamic>(
                queryDefinition: queryDef,
                requestOptions: new QueryRequestOptions()
                {
                    //MaxItemCount = 10
                });

            while (resultSet.HasMoreResults)
            {
                FeedResponse<dynamic> response = await resultSet.ReadNextAsync();

                Console.WriteLine($"RU Charge = {response.RequestCharge.ToString()}");
                foreach(var item in response)
                {
                    //Console.WriteLine(item);
                }
            }
        }


        static async Task<float[]> GetEmbeddingsAsync(string input)
        {

            float[] embedding = new float[0];
            
            EmbeddingsOptions options = new EmbeddingsOptions(embeddingsDeployment, new List<string> { input });

            //options.Dimensions = 1536;

            var response = await openAiClient.GetEmbeddingsAsync(options);

            Embeddings embeddings = response.Value;

            embedding = embeddings.Data[0].Embedding.ToArray();

            return embedding;
        }
    }
}

