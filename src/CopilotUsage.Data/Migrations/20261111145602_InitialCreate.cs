using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotUsage.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditUsageDaily",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UserLogin = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Credits = table.Column<decimal>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditUsageDaily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Seat",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Login = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PlanType = table.Column<string>(type: "TEXT", nullable: true),
                    AssigningTeam = table.Column<string>(type: "TEXT", nullable: true),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastActivityEditor = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seat", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeatSnapshot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PlanType = table.Column<string>(type: "TEXT", nullable: true),
                    TotalSeats = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedThisCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingInvitation = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingCancellation = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveThisCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    InactiveThisCycle = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatSnapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRun",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserMetricBreakdown",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UserLogin = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Dimension = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Interactions = table.Column<long>(type: "INTEGER", nullable: false),
                    Generations = table.Column<long>(type: "INTEGER", nullable: false),
                    Acceptances = table.Column<long>(type: "INTEGER", nullable: false),
                    LocAdded = table.Column<long>(type: "INTEGER", nullable: false),
                    LocDeleted = table.Column<long>(type: "INTEGER", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    Tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMetricBreakdown", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserMetricDaily",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UserLogin = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    AiCredits = table.Column<decimal>(type: "TEXT", nullable: false),
                    Prompts = table.Column<long>(type: "INTEGER", nullable: false),
                    SuggestionsGenerated = table.Column<long>(type: "INTEGER", nullable: false),
                    SuggestionsAccepted = table.Column<long>(type: "INTEGER", nullable: false),
                    LocSuggestedToAdd = table.Column<long>(type: "INTEGER", nullable: false),
                    LocSuggestedToDelete = table.Column<long>(type: "INTEGER", nullable: false),
                    LocAdded = table.Column<long>(type: "INTEGER", nullable: false),
                    LocDeleted = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedChat = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsedAgent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMetricDaily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserModelMonthly",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonthStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UserLogin = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Credits = table.Column<decimal>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserModelMonthly", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditUsageDaily_Day_UserLogin_Model",
                table: "CreditUsageDaily",
                columns: new[] { "Day", "UserLogin", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Seat_Login",
                table: "Seat",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeatSnapshot_Day",
                table: "SeatSnapshot",
                column: "Day",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMetricBreakdown_Day_UserLogin_Dimension_Key",
                table: "UserMetricBreakdown",
                columns: new[] { "Day", "UserLogin", "Dimension", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMetricBreakdown_UserLogin",
                table: "UserMetricBreakdown",
                column: "UserLogin");

            migrationBuilder.CreateIndex(
                name: "IX_UserMetricDaily_Day_UserLogin",
                table: "UserMetricDaily",
                columns: new[] { "Day", "UserLogin" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMetricDaily_UserLogin",
                table: "UserMetricDaily",
                column: "UserLogin");

            migrationBuilder.CreateIndex(
                name: "IX_UserModelMonthly_MonthStart_UserLogin_Model",
                table: "UserModelMonthly",
                columns: new[] { "MonthStart", "UserLogin", "Model" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditUsageDaily");

            migrationBuilder.DropTable(
                name: "Seat");

            migrationBuilder.DropTable(
                name: "SeatSnapshot");

            migrationBuilder.DropTable(
                name: "SyncRun");

            migrationBuilder.DropTable(
                name: "UserMetricBreakdown");

            migrationBuilder.DropTable(
                name: "UserMetricDaily");

            migrationBuilder.DropTable(
                name: "UserModelMonthly");
        }
    }
}
