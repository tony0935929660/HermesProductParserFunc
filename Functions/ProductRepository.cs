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
        bool ProductExists(string title, string price, string color);
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
            var appRoot = ResolveAppRoot();
            var configuredPath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH");
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                if (Path.IsPathRooted(configuredPath))
                {
                    return Path.GetFullPath(configuredPath);
                }

                return Path.GetFullPath(Path.Combine(appRoot, configuredPath));
            }

            return Path.Combine(appRoot, "data", "hermes.db");
        }

        private static string ResolveAppRoot()
        {
            var scriptRoot = Environment.GetEnvironmentVariable("AzureWebJobsScriptRoot");
            if (!string.IsNullOrWhiteSpace(scriptRoot))
            {
                return Path.GetFullPath(scriptRoot);
            }

            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (Directory.GetFiles(current.FullName, "*.csproj").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.GetFiles(current.FullName, "*.csproj").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return Directory.GetCurrentDirectory();
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
                    Color TEXT,
                    UNIQUE(Title, Price, Color)
                );
            ";
            cmd.ExecuteNonQuery();
        }
        public void ClearAllProducts()
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Product";
            cmd.ExecuteNonQuery();
        }
        public bool ProductExists(string title, string price, string color)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Title = $title AND Price = $price AND Color = $color";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$price", price);
            cmd.Parameters.AddWithValue("$color", color ?? "");
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
            cmd.CommandText = "INSERT OR IGNORE INTO Product (Title, Price, ImageUrl, Color) VALUES ($title, $price, $img, $color)";
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
                CREATE TABLE Product (
                    Id NVARCHAR(255) NOT NULL PRIMARY KEY,
                    Title NVARCHAR(255) NOT NULL,
                    Price NVARCHAR(255) NOT NULL,
                    ImageUrl NVARCHAR(1024),
                    Color NVARCHAR(255),
                    CONSTRAINT UQ_TitlePriceColor UNIQUE (Title, Price, Color)
                );
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
        public bool ProductExists(string title, string price, string color)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Title = @title AND Price = @price AND Color = @color";
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@price", price);
            cmd.Parameters.AddWithValue("@color", color ?? "");
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
