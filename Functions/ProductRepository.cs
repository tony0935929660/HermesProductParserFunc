using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Data.SqlClient;

namespace HermesProductParserFunc.Functions
{
    public interface IProductRepository
    {
        void InitDb();
        bool ProductExists(string title, string price);
        void InsertProduct(Product p);
        List<Product> GetAllProducts();
    }

    // SQLite 實作
    public class SqliteProductRepository : IProductRepository
    {
        private readonly string _connStr = "Data Source=hermes.db";
        public void InitDb()
        {
            Console.WriteLine("InitDb called, db path: " + System.IO.Path.GetFullPath("hermes.db"));
            
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Product (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Price TEXT NOT NULL,
                    ImageUrl TEXT,
                    UNIQUE(Title, Price)
                );
            ";
            cmd.ExecuteNonQuery();
        }
        public bool ProductExists(string title, string price)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Title = $title AND Price = $price";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$price", price);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        public void InsertProduct(Product p)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Product (Title, Price, ImageUrl) VALUES ($title, $price, $img)";
            cmd.Parameters.AddWithValue("$title", p.Title);
            cmd.Parameters.AddWithValue("$price", p.Price);
            cmd.Parameters.AddWithValue("$img", p.ImageUrl ?? "");
            cmd.ExecuteNonQuery();
        }
        public List<Product> GetAllProducts()
        {
            var list = new List<Product>();
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title, Price, ImageUrl FROM Product";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Product
                {
                    Title = reader.GetString(0),
                    Price = reader.GetString(1),
                    ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2)
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
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Product' AND xtype='U')
                CREATE TABLE Product (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Title NVARCHAR(255) NOT NULL,
                    Price NVARCHAR(255) NOT NULL,
                    ImageUrl NVARCHAR(1024),
                    CONSTRAINT UQ_TitlePrice UNIQUE (Title, Price)
                );
            ";
            cmd.ExecuteNonQuery();
        }
        public bool ProductExists(string title, string price)
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Product WHERE Title = @title AND Price = @price";
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@price", price);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        public void InsertProduct(Product p)
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF NOT EXISTS (SELECT 1 FROM Product WHERE Title = @title AND Price = @price)
                INSERT INTO Product (Title, Price, ImageUrl) VALUES (@title, @price, @img)";
            cmd.Parameters.AddWithValue("@title", p.Title);
            cmd.Parameters.AddWithValue("@price", p.Price);
            cmd.Parameters.AddWithValue("@img", p.ImageUrl ?? "");
            cmd.ExecuteNonQuery();
        }
        public List<Product> GetAllProducts()
        {
            var list = new List<Product>();
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title, Price, ImageUrl FROM Product";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Product
                {
                    Title = reader.GetString(0),
                    Price = reader.GetString(1),
                    ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            }
            return list;
        }
    }
}
