﻿using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.OpenAI;
using System.Reflection;
using Azure.Search.Documents;

namespace DotNetOfficeAzureApp.Services
{
    public class AzureAISearchService : IAzureAISearchService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AzureAISearchService> _logger;
        private readonly SearchIndexerClient _indexerClient;
        private readonly SearchIndexClient _indexClient;
        private readonly string _azureOpenAIApiBase;
        private readonly string _azureOpenAIKey;
        private readonly string _azuresearchServiceEndpoint;
        private readonly string _azuresearchApiKey;
        private readonly string _azureOpenAIDeploymentId;
        private readonly OpenAIClient _client;
        private ChatCompletionsOptions _options;

        private class ChatSettings
        {
            public int MaxTokens { get; set; } = 800;
            public float Temperature { get; set; } = 0.7f;
            public float TopP { get; set; } = 0.95f;
            public float FrequencyPenalty { get; set; } = 0;
            public float PresencePenalty { get; set; } = 0;
        }

        private readonly ChatSettings _chatSettings = new ChatSettings();

        public AzureAISearchService(IConfiguration configuration, ILogger<AzureAISearchService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _azuresearchServiceEndpoint = _configuration.GetValue<string>("AISearchServiceEndpoint");
            _azuresearchApiKey = _configuration.GetValue<string>("AISearchApiKey");
            _azureOpenAIApiBase = _configuration.GetValue<string>("AzOpenAIApiBase");
            _azureOpenAIKey = _configuration.GetValue<string>("AzOpenAIKey");
            _azureOpenAIDeploymentId = _configuration.GetValue<string>("AzOpenAIDeploymentId");

            // Configure client options for private endpoints
            var clientOptions = new OpenAIClientOptions
            {
                Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }))
            };

            // Configure search client options
            var searchClientOptions = new SearchClientOptions
            {
                Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }))
            };

            _client = new OpenAIClient(new Uri(_azureOpenAIApiBase), new AzureKeyCredential(_azureOpenAIKey), clientOptions);
            _indexerClient = new SearchIndexerClient(new Uri(_azuresearchServiceEndpoint),
                new AzureKeyCredential(_azuresearchApiKey), searchClientOptions);
            _indexClient = new SearchIndexClient(new Uri(_azuresearchServiceEndpoint),
                new AzureKeyCredential(_azuresearchApiKey), searchClientOptions);

            CreateChatCompletionOptions();
        }

        public bool UpdateIndexer()
        {
            try
            {
                return UpdateIndexerForContainer("general").Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating indexer");
                return false;
            }
        }

        public async Task<bool> UpdateIndexerForContainer(string containerName)
        {
            try
            {
                bool setupSuccess = await CreateAndConfigureDataSourceAndIndexer(containerName);
                if (!setupSuccess)
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating indexer for container {containerName}");
                return false;
            }
        }

        private async Task<bool> CreateAndConfigureDataSourceAndIndexer(string containerName)
        {
            try
            {
                string dataSourceName = $"azure-blob-{containerName}-datasource";
                string indexerName = $"vector-{containerName}-indexer";
                string indexName = $"vector-{containerName}-index";
                string connectionString = _configuration.GetSection("Storage")["connectionString"];

                // Create data source
                try
                {
                    var dataSource = new SearchIndexerDataSourceConnection(
                        dataSourceName,
                        SearchIndexerDataSourceType.AzureBlob,
                        connectionString,
                        new SearchIndexerDataContainer(containerName))
                    {
                        Description = $"Blob data source for {containerName} container"
                    };

                    await _indexerClient.CreateOrUpdateDataSourceConnectionAsync(dataSource);
                    _logger.LogInformation($"Data source {dataSourceName} created successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error creating data source for {containerName}");
                    return false;
                }

                // Create indexer
                try
                {
                    var indexerParameters = new IndexingParameters
                    {
                        MaxFailedItems = -1,
                        MaxFailedItemsPerBatch = -1
                    };

                    indexerParameters.Configuration.Add("dataToExtract", "contentAndMetadata");
                    indexerParameters.Configuration.Add("parsingMode", "default");

                    var indexer = new SearchIndexer(
                        indexerName,
                        dataSourceName,
                        indexName)
                    {
                        Description = $"Indexer for {containerName}",
                        Schedule = new IndexingSchedule(TimeSpan.FromMinutes(5)),
                        Parameters = indexerParameters
                    };

                    indexer.FieldMappings.Add(new FieldMapping("metadata_storage_name")
                    { TargetFieldName = "title" });
                    indexer.FieldMappings.Add(new FieldMapping("content")
                    { TargetFieldName = "chunk" });

                    // Try to delete existing indexer first
                    try
                    {
                        await _indexerClient.DeleteIndexerAsync(indexerName);
                        await Task.Delay(2000); // Wait for deletion to complete
                    }
                    catch { } // Ignore if it doesn't exist

                    await _indexerClient.CreateIndexerAsync(indexer);
                    _logger.LogInformation($"Indexer {indexerName} created successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error creating indexer for {containerName}");
                    return false;
                }

                // Create search index
                try
                {
                    var fields = new List<SearchField>
                    {
                        new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                        new SearchField("content", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true,
                            AnalyzerName = "standard.lucene"
                        },
                        new SearchField("title", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true,
                            AnalyzerName = "standard.lucene"
                        },
                        new SearchField("chunk", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true
                        }
                    };

                    var index = new SearchIndex(indexName, fields);
                    await _indexClient.CreateOrUpdateIndexAsync(index);
                    _logger.LogInformation($"Search index {indexName} created successfully");

                    // Run the indexer
                    await _indexerClient.RunIndexerAsync(indexerName);
                    _logger.LogInformation($"Indexer {indexerName} started running");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating search index");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in CreateAndConfigureDataSourceAndIndexer for {containerName}");
                return false;
            }
        }

        public async Task<Response<ChatCompletions>> SearchResultByOpenAI(string chatInput, string containerName = "general")
        {
            try
            {
                var indexName = $"vector-{containerName}-index";
                _logger.LogInformation($"Searching in index: {indexName}");

                // Configure client with certificate validation for private endpoints
                var clientOptions = new OpenAIClientOptions
                {
                    Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    }))
                };

                var client = new OpenAIClient(
                    new Uri(_azureOpenAIApiBase),
                    new AzureKeyCredential(_azureOpenAIKey),
                    clientOptions);

                var options = new ChatCompletionsOptions();

                // Set properties that are common across versions
                options.Temperature = _chatSettings.Temperature;
                options.MaxTokens = _chatSettings.MaxTokens;
                options.FrequencyPenalty = _chatSettings.FrequencyPenalty;
                options.PresencePenalty = _chatSettings.PresencePenalty;

                // Add messages
                options.Messages.Add(new ChatMessage(ChatRole.System,
                    "You are a helpful assistant. Provide clear and informative responses based on the available documents."));
                options.Messages.Add(new ChatMessage(ChatRole.User, chatInput));

                // Set Azure extensions
                options.AzureExtensionsOptions = new AzureChatExtensionsOptions();

                // Create the search extension
                var searchExtension = new AzureCognitiveSearchChatExtensionConfiguration();

                // Set properties using direct assignment where possible
                searchExtension.SearchEndpoint = new Uri(_azuresearchServiceEndpoint);
                searchExtension.IndexName = indexName;

                // For the API key, we need to use the property setter method
                Type extensionType = searchExtension.GetType();
                extensionType.GetMethod("set_SearchKey")?.Invoke(
                    searchExtension,
                    new object[] { new AzureKeyCredential(_azuresearchApiKey) });

                options.AzureExtensionsOptions.Extensions.Add(searchExtension);

                string chatDeploymentId = _configuration.GetValue<string>("AzOpenAIChatDeploymentId") ?? "gpt-4o";

                // For this version, you need to set the deployment name on the options
                options.DeploymentName = chatDeploymentId;

                // Call method without deployment parameter
                return await client.GetChatCompletionsAsync(options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchResultByOpenAI. Container: {Container}, Query: {Query}",
                    containerName, chatInput);
                throw;
            }
        }

        private void CreateChatCompletionOptions()
        {
            _options = new ChatCompletionsOptions()
            {
                Temperature = _chatSettings.Temperature,
                MaxTokens = _chatSettings.MaxTokens,
                FrequencyPenalty = _chatSettings.FrequencyPenalty,
                PresencePenalty = _chatSettings.PresencePenalty,
            };

            _options.AzureExtensionsOptions = new AzureChatExtensionsOptions();
            var searchExtension = new AzureCognitiveSearchChatExtensionConfiguration
            {
                SearchEndpoint = new Uri(_azuresearchServiceEndpoint),
                IndexName = "vector-general-index"
            };

            // Try to set the API key using reflection
            foreach (var propName in new[] { "SearchKey", "Key", "ApiKey", "KeyCredential" })
            {
                var prop = searchExtension.GetType().GetProperty(propName);
                if (prop != null)
                {
                    try
                    {
                        prop.SetValue(searchExtension, new AzureKeyCredential(_azuresearchApiKey));
                        break;
                    }
                    catch { }
                }
            }

            _options.AzureExtensionsOptions.Extensions.Add(searchExtension);
        }
    }
}