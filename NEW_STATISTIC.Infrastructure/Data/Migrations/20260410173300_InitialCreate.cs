using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NEW_STATISTIC.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Candles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TriggerTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowStartMs = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEndMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTradeTimeInWindowMs = table.Column<long>(type: "INTEGER", nullable: false),
                    MinPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    DiffPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    FirstTradePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalQuoteUsdt = table.Column<decimal>(type: "TEXT", nullable: false),
                    DensityUsdtPerMs = table.Column<decimal>(type: "TEXT", nullable: false),
                    FollowUpJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Simulations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CandleId = table.Column<long>(type: "INTEGER", nullable: false),
                    OpenOffsetPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    OpenPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    TakeProfitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopLossPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    OutcomesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Simulations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Simulations_Candles_CandleId",
                        column: x => x.CandleId,
                        principalTable: "Candles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Candles_Exchange_CreatedAt",
                table: "Candles",
                columns: new[] { "Exchange", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Candles_Symbol_TriggerTimeMs",
                table: "Candles",
                columns: new[] { "Symbol", "TriggerTimeMs" });

            migrationBuilder.CreateIndex(
                name: "IX_Simulations_CandleId",
                table: "Simulations",
                column: "CandleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Simulations");

            migrationBuilder.DropTable(
                name: "Candles");
        }
    }
}
