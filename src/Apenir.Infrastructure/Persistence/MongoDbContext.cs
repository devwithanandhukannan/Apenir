using System;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Apenir.Application.Common.Models;
using Apenir.Core.Entities;

namespace Apenir.Infrastructure.Persistence
{
    public class MongoDbContext
    {
        private readonly IMongoDatabase _database;

        static MongoDbContext()
        {
            // Register GuidSerializer so Guid maps properly to MongoDB UUID subtype 4
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }

        public MongoDbContext(IOptions<MongoSettings> settings)
        {
            var clientSettings = MongoClientSettings.FromConnectionString(settings.Value.ConnectionString);
            // Ensure Guid representation is standard
            clientSettings.GuidRepresentation = GuidRepresentation.Standard;
            
            var client = new MongoClient(clientSettings);
            _database = client.GetDatabase(settings.Value.DatabaseName);

            ConfigureCollections();
        }

        public IMongoCollection<Admin> Admins => _database.GetCollection<Admin>("Admins");
        public IMongoCollection<RefreshToken> RefreshTokens => _database.GetCollection<RefreshToken>("RefreshTokens");

        private void ConfigureCollections()
        {
            // Unique Email Index for Admin
            var adminEmailKey = Builders<Admin>.IndexKeys.Ascending(a => a.Email);
            var adminEmailIndexOptions = new CreateIndexOptions { Unique = true };
            Admins.Indexes.CreateOne(new CreateIndexModel<Admin>(adminEmailKey, adminEmailIndexOptions));

            // Index on Refresh Token
            var tokenKey = Builders<RefreshToken>.IndexKeys.Ascending(t => t.Token);
            var tokenIndexOptions = new CreateIndexOptions { Unique = true };
            RefreshTokens.Indexes.CreateOne(new CreateIndexModel<RefreshToken>(tokenKey, tokenIndexOptions));

            // TTL index for Refresh Tokens (expires automatically at ExpiresAt)
            var ttlKey = Builders<RefreshToken>.IndexKeys.Ascending(t => t.ExpiresAt);
            var ttlIndexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.Zero };
            RefreshTokens.Indexes.CreateOne(new CreateIndexModel<RefreshToken>(ttlKey, ttlIndexOptions));
        }
    }
}
