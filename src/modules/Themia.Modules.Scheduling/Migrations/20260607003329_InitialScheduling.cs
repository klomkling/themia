using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Themia.Modules.Scheduling.Migrations
{
    /// <inheritdoc />
    public partial class InitialScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "execution_history",
                schema: "scheduling",
                columns: table => new
                {
                    fire_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scheduler_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scheduler_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    job = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    trigger = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    scheduled_fire_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actual_fire_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recovering = table.Column<bool>(type: "boolean", nullable: false),
                    vetoed = table.Column<bool>(type: "boolean", nullable: false),
                    finished_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exception_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_history", x => x.fire_instance_id);
                });

            migrationBuilder.CreateTable(
                name: "scheduler_stats",
                schema: "scheduling",
                columns: table => new
                {
                    scheduler_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    total_jobs_executed = table.Column<int>(type: "integer", nullable: false),
                    total_jobs_failed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduler_stats", x => x.scheduler_name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_execution_history_scheduler_trigger_fired",
                schema: "scheduling",
                table: "execution_history",
                columns: new[] { "scheduler_name", "trigger", "actual_fire_time_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_history",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "scheduler_stats",
                schema: "scheduling");
        }
    }
}
