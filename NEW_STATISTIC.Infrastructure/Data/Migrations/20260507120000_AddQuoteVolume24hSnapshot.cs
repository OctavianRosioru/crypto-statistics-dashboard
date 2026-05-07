using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NEW_STATISTIC.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(StatisticDbContext))]
    [Migration("20260507120000_AddQuoteVolume24hSnapshot")]
    public partial class AddQuoteVolume24hSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hUsdt",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuoteVolume24hUpdatedMs",
                table: "Candles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange1mPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange5mPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange15mPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange30mPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange1hPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange3hPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange6hPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange12hPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume24hChange24hPct",
                table: "Candles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "QuoteVolume24hUsdt", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hUpdatedMs", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange1mPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange5mPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange15mPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange30mPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange1hPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange3hPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange6hPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange12hPct", table: "Candles");
            migrationBuilder.DropColumn(name: "QuoteVolume24hChange24hPct", table: "Candles");
        }
    }
}
