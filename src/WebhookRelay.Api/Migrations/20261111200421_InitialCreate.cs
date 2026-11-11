using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebhookRelay.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyUsage",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CallsUsed = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyUsage", x => x.Day);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeliveryId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempts_Events_WebhookEventId",
                        column: x => x.WebhookEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_WebhookEventId",
                table: "DeliveryAttempts",
                column: "WebhookEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Source_DeliveryId",
                table: "Events",
                columns: new[] { "Source", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_Status_NextAttemptAt",
                table: "Events",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyUsage");

            migrationBuilder.DropTable(
                name: "DeliveryAttempts");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
