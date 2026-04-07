using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Controllers;
using SocialMediaPanel.Models;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<ConnectedAccount> ConnectedAccounts { get; set; }
        public DbSet<WebhookEvent> WebhookEvents { get; set; }
        public DbSet<Lead> Leads { get; set; }
        public DbSet<PageComment> PageComments { get; set; }
        public DbSet<PageMessage> PageMessages { get; set; }
        public DbSet<PostInsight> PostInsights { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("usersinfo");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Email).IsUnique();

                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
                entity.Property(e => e.Email).HasColumnName("email").IsRequired().HasMaxLength(150);
                entity.Property(e => e.Password).HasColumnName("password").IsRequired().HasMaxLength(255);
                entity.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(200);
                entity.Property(e => e.CompanySize).HasColumnName("company_size").HasMaxLength(50);
                entity.Property(e => e.CompanyType).HasColumnName("company_type").HasMaxLength(100);
                entity.Property(e => e.CompanyAddress).HasColumnName("company_address");
            });
            // ── CONNECTED ACCOUNTS ──
            modelBuilder.Entity<ConnectedAccount>(entity =>
            {
                entity.ToTable("connected_accounts");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.Platform, e.AccountId }).IsUnique();
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.UserId).HasColumnName("user_id");
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(20);
                entity.Property(e => e.AccountId).HasColumnName("account_id").IsRequired().HasMaxLength(100);
                entity.Property(e => e.AccountName).HasColumnName("account_name").IsRequired().HasMaxLength(200);
                entity.Property(e => e.AccessToken).HasColumnName("access_token");
                entity.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at");
                entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // ── WEBHOOK EVENTS ──
            modelBuilder.Entity<WebhookEvent>(entity =>
            {
                entity.ToTable("webhook_events");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(20);
                entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired().HasMaxLength(100);
                entity.Property(e => e.ObjectId).HasColumnName("object_id").HasMaxLength(100);
                entity.Property(e => e.RawPayload).HasColumnName("raw_payload").IsRequired();
                entity.Property(e => e.Processed).HasColumnName("processed").HasDefaultValue(false);
                entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // ── LEADS ──
            modelBuilder.Entity<Lead>(entity =>
            {
                entity.ToTable("leads");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.LeadId).IsUnique();
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.LeadId).HasColumnName("lead_id").IsRequired().HasMaxLength(100);
                entity.Property(e => e.PageId).HasColumnName("page_id").HasMaxLength(100);
                entity.Property(e => e.FormId).HasColumnName("form_id").HasMaxLength(100);
                entity.Property(e => e.FullName).HasColumnName("full_name").HasMaxLength(200);
                entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(150);
                entity.Property(e => e.Phone).HasColumnName("phone").HasMaxLength(50);
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(20);
                entity.Property(e => e.RawData).HasColumnName("raw_data");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // ── PAGE COMMENTS ──
            modelBuilder.Entity<PageComment>(entity =>
            {
                entity.ToTable("page_comments");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.CommentId).IsUnique();
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CommentId).HasColumnName("comment_id").IsRequired().HasMaxLength(100);
                entity.Property(e => e.PageId).HasColumnName("page_id").HasMaxLength(100);
                entity.Property(e => e.PostId).HasColumnName("post_id").HasMaxLength(100);
                entity.Property(e => e.SenderId).HasColumnName("sender_id").HasMaxLength(100);
                entity.Property(e => e.SenderName).HasColumnName("sender_name").HasMaxLength(200);
                entity.Property(e => e.Message).HasColumnName("message");
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(20);
                entity.Property(e => e.CommentType).HasColumnName("comment_type").HasMaxLength(30).HasDefaultValue("comment");
                entity.Property(e => e.CommentTime).HasColumnName("comment_time");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // ── PAGE MESSAGES ──
            modelBuilder.Entity<PageMessage>(entity =>
            {
                entity.ToTable("page_messages");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.MessageId).IsUnique();
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired().HasMaxLength(100);
                entity.Property(e => e.PageId).HasColumnName("page_id").HasMaxLength(100);
                entity.Property(e => e.SenderId).HasColumnName("sender_id").HasMaxLength(100);
                entity.Property(e => e.SenderName).HasColumnName("sender_name").HasMaxLength(200);
                entity.Property(e => e.MessageText).HasColumnName("message_text");
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(30);
                entity.Property(e => e.MessageTime).HasColumnName("message_time");
                entity.Property(e => e.IsReplied).HasColumnName("is_replied").HasDefaultValue(false);
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // ── POST INSIGHTS ──
            modelBuilder.Entity<PostInsight>(entity =>
            {
                entity.ToTable("post_insights");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.PostId, e.Platform }).IsUnique();
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.PostId).HasColumnName("post_id").IsRequired().HasMaxLength(100);
                entity.Property(e => e.PageId).HasColumnName("page_id").HasMaxLength(100);
                entity.Property(e => e.Platform).HasColumnName("platform").IsRequired().HasMaxLength(20);
                entity.Property(e => e.LikesCount).HasColumnName("likes_count").HasDefaultValue(0);
                entity.Property(e => e.CommentsCount).HasColumnName("comments_count").HasDefaultValue(0);
                entity.Property(e => e.SharesCount).HasColumnName("shares_count").HasDefaultValue(0);
                entity.Property(e => e.Reach).HasColumnName("reach").HasDefaultValue(0);
                entity.Property(e => e.Impressions).HasColumnName("impressions").HasDefaultValue(0);
                entity.Property(e => e.SavesCount).HasColumnName("saves_count").HasDefaultValue(0);
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
        public DbSet<SocialMediaPanel.ViewModels.LeadViewModel> LeadViewModel { get; set; } = default!;

        public DbSet<GmailIntegration> GmailIntegrations { get; set; }
        public DbSet<TwitterAccount> TwitterAccounts { get; set; }
        public DbSet<TweetPosted> TweetsPosted { get; set; }

        public DbSet<PageReply> PageReplies { get; set; }

        public DbSet<UserToken> UserTokens { get; set; }

        public DbSet<FacebookPageEntity> FacebookPages { get; set; }

        public DbSet<SocialPost> SocialPosts { get; set; }
        public DbSet<InstagramAccount> InstagramAccounts { get; set; }
        public DbSet<LinkedInIntegration> LinkedInIntegrations { get; set; }
        public DbSet<LinkedinPosts> LinkedInPosts { get; set; }
    }
}