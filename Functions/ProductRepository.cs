using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace HermesProductParserFunc.Functions
{
    public interface IProductRepository
    {
        void InitDb();
        bool ProductExists(string id);
        void InsertProduct(Product p);
        List<Product> GetAllProducts();
        void ClearAllProducts();
        void InsertAllProducts(List<Product> products);
    }

    // SQLite 實作
    public class SqliteProductRepository : IProductRepository
    {
        private readonly string _dbPath;
        private readonly string _connStr;

        public SqliteProductRepository()
        {
            _dbPath = ResolveDbPath();
            var dbDirectory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
            }

            _connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath
            }.ToString();
        }

        private static string ResolveDbPath()
        {
            var configuredPath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH");
            return AppPathResolver.ResolvePath(configuredPath, "data", "hermes.db");
        }

        public void InitDb()
        {
            Console.WriteLine("InitDb called, db path: " + _dbPath);
            
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Product (
                    Id TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Price TEXT NOT NULL,
                    ImageUrl TEXT,
                    Color TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            EnsureSqliteSchema(conn);
        }
        public void ClearAllProducts()
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Product";
            cmd.ExecuteNonQuery();
        }
        public bool ProductExists(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        public void InsertAllProducts(List<Product> products)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var tran = conn.BeginTransaction();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Product (Id, Title, Price, ImageUrl, Color) VALUES ($id, $title, $price, $img, $color)";
            foreach (var p in products)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", p.Id);
                cmd.Parameters.AddWithValue("$title", p.Title);
                cmd.Parameters.AddWithValue("$price", p.Price);
                cmd.Parameters.AddWithValue("$img", p.ImageUrl ?? "");
                cmd.Parameters.AddWithValue("$color", p.Color ?? "");
                cmd.ExecuteNonQuery();
            }
            tran.Commit();
        }

        public void InsertProduct(Product p)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Product (Id, Title, Price, ImageUrl, Color) VALUES ($id, $title, $price, $img, $color)";
            cmd.Parameters.AddWithValue("$id", p.Id);
            cmd.Parameters.AddWithValue("$title", p.Title);
            cmd.Parameters.AddWithValue("$price", p.Price);
            cmd.Parameters.AddWithValue("$img", p.ImageUrl ?? "");
            cmd.Parameters.AddWithValue("$color", p.Color ?? "");
            cmd.ExecuteNonQuery();
        }
        public List<Product> GetAllProducts()
        {
            var list = new List<Product>();
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title, Price, ImageUrl, Color FROM Product";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Product
                {
                    Id = reader.GetString(0).ToString(),
                    Title = reader.GetString(1),
                    Price = reader.GetString(2),
                    ImageUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Color = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            return list;
        }

        private static void EnsureSqliteSchema(SqliteConnection conn)
        {
            var schemaCommand = conn.CreateCommand();
            schemaCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Product'";
            var schemaSql = schemaCommand.ExecuteScalar()?.ToString() ?? string.Empty;
            if (!schemaSql.Contains("UNIQUE(Title, Price, Color)", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var transaction = conn.BeginTransaction();

            var renameCommand = conn.CreateCommand();
            renameCommand.Transaction = transaction;
            renameCommand.CommandText = "ALTER TABLE Product RENAME TO Product_Legacy";
            renameCommand.ExecuteNonQuery();

            var createCommand = conn.CreateCommand();
            createCommand.Transaction = transaction;
            createCommand.CommandText = @"
                CREATE TABLE Product (
                    Id TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Price TEXT NOT NULL,
                    ImageUrl TEXT,
                    Color TEXT
                );
            ";
            createCommand.ExecuteNonQuery();

            var copyCommand = conn.CreateCommand();
            copyCommand.Transaction = transaction;
            copyCommand.CommandText = @"
                INSERT OR IGNORE INTO Product (Id, Title, Price, ImageUrl, Color)
                SELECT
                    CASE
                        WHEN IFNULL(Id, '') = '' THEN lower(hex(randomblob(16)))
                        ELSE Id
                    END,
                    Title,
                    Price,
                    ImageUrl,
                    Color
                FROM Product_Legacy
            ";
            copyCommand.ExecuteNonQuery();

            var dropCommand = conn.CreateCommand();
            dropCommand.Transaction = transaction;
            dropCommand.CommandText = "DROP TABLE Product_Legacy";
            dropCommand.ExecuteNonQuery();

            transaction.Commit();
        }
    }

    // Azure SQL 實作
    public class AzureSqlProductRepository : IProductRepository
    {
        private readonly string _connStr;
        public AzureSqlProductRepository(string connStr) { _connStr = connStr; }
        public void InitDb()
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Product' AND xtype='U')
                BEGIN
                    CREATE TABLE Product (
                        Id NVARCHAR(255) NOT NULL PRIMARY KEY,
                        Title NVARCHAR(255) NOT NULL,
                        Price NVARCHAR(255) NOT NULL,
                        ImageUrl NVARCHAR(1024),
                        Color NVARCHAR(255)
                    );
                END;

                IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE [name] = 'UQ_TitlePriceColor' AND [parent_object_id] = OBJECT_ID('Product'))
                BEGIN
                    ALTER TABLE Product DROP CONSTRAINT UQ_TitlePriceColor;
                END;
            ";
            cmd.ExecuteNonQuery();
        }
        public void ClearAllProducts()
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Product";
            cmd.ExecuteNonQuery();
        }
        public bool ProductExists(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        public void InsertAllProducts(List<Product> products)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            using var tran = conn.BeginTransaction();
            var cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = @"IF NOT EXISTS (SELECT 1 FROM Product WHERE Id = @id)
                INSERT INTO Product (Id, Title, Price, ImageUrl, Color) VALUES (@id, @title, @price, @img, @color)";
            foreach (var p in products)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", p.Id ?? string.Empty);
                cmd.Parameters.AddWithValue("@title", p.Title);
                cmd.Parameters.AddWithValue("@price", p.Price);
                cmd.Parameters.AddWithValue("@img", p.ImageUrl ?? "");
                cmd.Parameters.AddWithValue("@color", p.Color ?? "");
                cmd.ExecuteNonQuery();
            }
            tran.Commit();
        }

        public void InsertProduct(Product p)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF NOT EXISTS (SELECT 1 FROM Product WHERE Id = @id)
                INSERT INTO Product (Id, Title, Price, ImageUrl, Color) VALUES (@id, @title, @price, @img, @color)";
            cmd.Parameters.AddWithValue("@id", p.Id ?? string.Empty);
            cmd.Parameters.AddWithValue("@title", p.Title);
            cmd.Parameters.AddWithValue("@price", p.Price);
            cmd.Parameters.AddWithValue("@img", p.ImageUrl ?? "");
            cmd.Parameters.AddWithValue("@color", p.Color ?? "");
            cmd.ExecuteNonQuery();
        }
        public List<Product> GetAllProducts()
        {
            var list = new List<Product>();
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title, Price, ImageUrl, Color FROM Product";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Product
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Price = reader.GetString(2),
                    ImageUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Color = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            return list;
        }
    }
}
