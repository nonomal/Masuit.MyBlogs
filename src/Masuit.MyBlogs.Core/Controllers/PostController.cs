﻿using System.Collections.Frozen;
using Dispose.Scope;
using Hangfire;
using Masuit.LuceneEFCore.SearchEngine;
using Masuit.LuceneEFCore.SearchEngine.Interfaces;
using Masuit.MyBlogs.Core.Common.Mails;
using Masuit.MyBlogs.Core.Configs;
using Masuit.MyBlogs.Core.Extensions;
using Masuit.MyBlogs.Core.Extensions.Firewall;
using Masuit.MyBlogs.Core.Extensions.Hangfire;
using Masuit.Tools.AspNetCore.ModelBinder;
using Masuit.Tools.AspNetCore.ResumeFileResults.Extensions;
using Masuit.Tools.Core.Validator;
using Masuit.Tools.Excel;
using Masuit.Tools.Html;
using Masuit.Tools.Logging;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;
using System.Linq.Dynamic.Core;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EFCoreSecondLevelCacheInterceptor;
using FreeRedis;
using Masuit.MyBlogs.Core.Models;
using Masuit.Tools.Core;
using Masuit.Tools.Mime;
using Masuit.Tools.TextDiff;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

namespace Masuit.MyBlogs.Core.Controllers;

/// <summary>
/// 文章管理
/// </summary>
public sealed class PostController : BaseController
{
    public IPostService PostService { get; set; }

    public ICategoryService CategoryService { get; set; }

    public ISeminarService SeminarService { get; set; }

    public IPostHistoryVersionService PostHistoryVersionService { get; set; }

    public IWebHostEnvironment HostEnvironment { get; set; }

    public ISearchEngine<DataContext> SearchEngine { get; set; }

    public ImagebedClient ImagebedClient { get; set; }

    public IPostVisitRecordService PostVisitRecordService { get; set; }

    public ICommentService CommentService { get; set; }

    public IPostTagService PostTagService { get; set; }

    /// <summary>
    /// 文章详情页
    /// </summary>
    /// <returns></returns>
    [Route("{id:int}"), Route("{id:int}/comments/{cid:int}"), ResponseCache(Duration = 600, VaryByHeader = "Cookie")]
    public async Task<ActionResult> Details([FromServices] ISearchDetailsService searchService, int id, string kw, int cid, string t)
    {
        if (Request.IsRobot())
        {
            return View("Details_SEO", PostService[id] ?? throw new NotFoundException("文章未找到"));
        }

        if (string.IsNullOrEmpty(t))
        {
            return RedirectToAction("Details", cid > 0 ? new { id, kw, cid, t = HttpContext.Connection.Id } : new { id, kw, t = HttpContext.Connection.Id });
        }

        var post = await PostService.GetQuery(p => p.Id == id && (p.Status == Status.Published || CurrentUser.IsAdmin)).Include(p => p.Seminar).AsNoTracking().FirstOrDefaultAsync() ?? throw new NotFoundException("文章未找到");
        CheckPermission(post);
        var ip = ClientIP.ToString();
        if (!string.IsNullOrEmpty(post.Redirect))
        {
            if (string.IsNullOrEmpty(HttpContext.Session.Get<string>("post" + id)))
            {
                BackgroundJob.Enqueue<IHangfireBackJob>(job => job.RecordPostVisit(id, ip, Request.Headers[HeaderNames.Referer].ToString(), Request.GetDisplayUrl()));
                HttpContext.Session.Set("post" + id, id.ToString());
            }

            return Redirect(post.Redirect);
        }

        post.Category = CategoryService[post.CategoryId];
        ViewBag.CommentsCount = CommentService.Count(c => c.PostId == id && c.ParentId == null && c.Status == Status.Published);
        ViewBag.HistoryCount = PostHistoryVersionService.Count(c => c.PostId == id);
        ViewBag.Keyword = post.Keyword + "," + post.Label;
        ViewBag.Desc = await post.Content.GetSummary(200);
        var modifyDate = post.ModifyDate;
        ViewBag.Next = await PostService.GetQuery(p => p.ModifyDate > modifyDate && (p.LimitMode ?? 0) == RegionLimitMode.All && (p.Status == Status.Published || CurrentUser.IsAdmin), p => p.ModifyDate).ProjectModelBase().Cacheable().FirstOrDefaultAsync();
        ViewBag.Prev = await PostService.GetQuery(p => p.ModifyDate < modifyDate && (p.LimitMode ?? 0) == RegionLimitMode.All && (p.Status == Status.Published || CurrentUser.IsAdmin), p => p.ModifyDate, false).ProjectModelBase().Cacheable().FirstOrDefaultAsync();
        ViewData[nameof(post.Author)] = post.Author;
        ViewData[nameof(post.PostDate)] = post.PostDate;
        ViewData[nameof(post.ModifyDate)] = post.ModifyDate;
        ViewData["cover"] = post.Content.MatchFirstImgSrc();
        if (!string.IsNullOrEmpty(kw))
        {
            await PostService.Highlight(post, kw);
        }

        var keys = searchService.GetQuery(e => e.IP == ip).OrderByDescending(e => e.SearchTime).Select(e => e.Keywords).Distinct().Take(5).Cacheable().ToPooledListScope();
        var regex = SearchEngine.LuceneIndexSearcher.CutKeywords(string.IsNullOrWhiteSpace(post.Keyword + post.Label) ? post.Title : post.Keyword + post.Label).Union(keys).Select(Regex.Escape).Join("|");
        ViewBag.Ads = AdsService.GetByWeightedPrice(AdvertiseType.InPage, Request.Location(), post.CategoryId, regex);
        ViewBag.Related = PostService.GetQuery(PostBaseWhere().And(p => p.Id != post.Id && Regex.IsMatch(p.Title + (p.Keyword ?? "") + (p.Label ?? ""), regex, RegexOptions.IgnoreCase)), p => p.AverageViewCount, false).Take(10).Select(p => new { p.Id, p.Title }).Cacheable().ToFrozenDictionary(p => p.Id, p => p.Title);

        post.ModifyDate = post.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        post.PostDate = post.PostDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        post.Content = await ReplaceVariables(post.Content).Next(s => post.DisableCopy ? s.InjectFingerprint(ip) : Task.FromResult(s));
        post.ProtectContent = await ReplaceVariables(post.ProtectContent).Next(s => post.DisableCopy ? s.InjectFingerprint(ip) : Task.FromResult(s));

        if (CurrentUser.IsAdmin)
        {
            return View("Details_Admin", post);
        }

        if (string.IsNullOrEmpty(HttpContext.Session.Get<string>("post" + id)))
        {
            BackgroundJob.Enqueue<IHangfireBackJob>(job => job.RecordPostVisit(id, ip, Request.Headers[HeaderNames.Referer].ToString(), Request.GetDisplayUrl()));
            HttpContext.Session.Set("post" + id, id.ToString());
        }

        if (post.LimitMode == RegionLimitMode.OnlyForSearchEngine)
        {
            BackgroundJob.Enqueue<IHangfireBackJob>(job => job.RecordPostVisit(id, ip, Request.Headers[HeaderNames.Referer].ToString(), Request.GetDisplayUrl()));
        }

        return View(post);
    }

    /// <summary>
    /// 文章历史版本
    /// </summary>
    /// <param name="id"></param>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    [Route("{id:int}/history"), ResponseCache(Duration = 600, VaryByQueryKeys = new[] { "id", "page", "size" }, VaryByHeader = "Cookie")]
    public async Task<ActionResult> History(int id, [Range(1, int.MaxValue, ErrorMessage = "页码必须大于0")] int page = 1, [Range(1, 50, ErrorMessage = "页大小必须在0到50之间")] int size = 20)
    {
        var post = await PostService.GetAsync(p => p.Id == id && (p.Status == Status.Published || CurrentUser.IsAdmin)) ?? throw new NotFoundException("文章未找到");
        CheckPermission(post);
        ViewBag.Primary = post;
        var list = await PostHistoryVersionService.GetPagesAsync(page, size, v => v.PostId == id, v => v.ModifyDate, false);
        foreach (var item in list.Data)
        {
            item.ModifyDate = item.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        }

        ViewBag.Ads = AdsService.GetByWeightedPrice(AdvertiseType.InPage, Request.Location(), post.CategoryId, post.Keyword + "," + post.Label);
        return View(list);
    }

    /// <summary>
    /// 文章历史版本
    /// </summary>
    /// <param name="id"></param>
    /// <param name="hid"></param>
    /// <returns></returns>
    [Route("{id:int}/history/{hid:int}"), ResponseCache(Duration = 600, VaryByQueryKeys = new[] { "id", "hid" }, VaryByHeader = "Cookie")]
    public async Task<ActionResult> HistoryVersion(int id, int hid)
    {
        var history = await PostHistoryVersionService.GetAsync(v => v.Id == hid && (v.Post.Status == Status.Published || CurrentUser.IsAdmin)) ?? throw new NotFoundException("文章未找到");
        CheckPermission(history.Post);
        history.Content = await ReplaceVariables(history.Content).Next(s => CurrentUser.IsAdmin || Request.IsRobot() ? Task.FromResult(s) : s.InjectFingerprint(ClientIP.ToString()));
        history.ProtectContent = await ReplaceVariables(history.ProtectContent).Next(s => CurrentUser.IsAdmin || Request.IsRobot() ? Task.FromResult(s) : s.InjectFingerprint(ClientIP.ToString()));
        history.ModifyDate = history.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        var next = await PostHistoryVersionService.GetAsync(p => p.PostId == id && p.ModifyDate > history.ModifyDate, p => p.ModifyDate);
        var prev = await PostHistoryVersionService.GetAsync(p => p.PostId == id && p.ModifyDate < history.ModifyDate, p => p.ModifyDate, false);
        ViewBag.Next = next;
        ViewBag.Prev = prev;
        ViewBag.Ads = AdsService.GetByWeightedPrice(AdvertiseType.InPage, Request.Location(), history.CategoryId, history.Label);
        ViewData[nameof(history.Post.Author)] = history.Post.Author;
        ViewData[nameof(history.Post.PostDate)] = history.Post.PostDate;
        ViewData[nameof(history.ModifyDate)] = history.ModifyDate;
        ViewData["cover"] = history.Content.MatchFirstImgSrc();
        return CurrentUser.IsAdmin ? View("HistoryVersion_Admin", history) : View(history);
    }

    /// <summary>
    /// 版本对比
    /// </summary>
    /// <param name="id"></param>
    /// <param name="v1"></param>
    /// <param name="v2"></param>
    /// <returns></returns>
    [Route("{id:int}/history/{v1:int}-{v2:int}"), ResponseCache(Duration = 600, VaryByQueryKeys = new[] { "id", "v1", "v2" }, VaryByHeader = "Cookie")]
    public async Task<ActionResult> CompareVersion(int id, int v1, int v2)
    {
        var post = await PostService.GetAsync(p => p.Id == id && (p.Status == Status.Published || CurrentUser.IsAdmin));
        var main = post.ToHistoryVersion() ?? throw new NotFoundException("文章未找到");
        CheckPermission(post);
        var right = v1 <= 0 ? main : await PostHistoryVersionService.GetAsync(v => v.Id == v1) ?? throw new NotFoundException("文章未找到");
        var left = v2 <= 0 ? main : await PostHistoryVersionService.GetAsync(v => v.Id == v2) ?? throw new NotFoundException("文章未找到");
        main.Id = id;
        var (html1, html2) = left.Content.HtmlDiff(right.Content);
        left.Content = await ReplaceVariables(html1).Next(s => CurrentUser.IsAdmin || Request.IsRobot() ? Task.FromResult(s) : s.InjectFingerprint(ClientIP.ToString()));
        left.ModifyDate = left.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        right.Content = await ReplaceVariables(html2).Next(s => CurrentUser.IsAdmin || Request.IsRobot() ? Task.FromResult(s) : s.InjectFingerprint(ClientIP.ToString()));
        right.ModifyDate = right.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        ViewBag.Ads = AdsService.GetsByWeightedPrice(2, AdvertiseType.InPage, Request.Location(), main.CategoryId, main.Label);
        ViewBag.DisableCopy = post.DisableCopy;
        return View(new[] { main, left, right });
    }

    /// <summary>
    /// 反对
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpPost("/post/votedown/{id}")]
    public async Task<ActionResult> VoteDown(int id)
    {
        if (HttpContext.Session.Get("post-vote" + id) != null)
        {
            return ResultData(null, false, "您刚才已经投过票了，感谢您的参与！");
        }

        var b = await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.VoteDownCount, m => m.VoteDownCount + 1)) > 0;
        if (b)
        {
            HttpContext.Session.Set("post-vote" + id, id.GetBytes());
        }

        return ResultData(null, b, b ? "投票成功！" : "投票失败！");
    }

    /// <summary>
    /// 支持
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpPost("/post/voteup/{id}")]
    public async Task<ActionResult> VoteUp(int id)
    {
        if (HttpContext.Session.Get("post-vote" + id) != null)
        {
            return ResultData(null, false, "您刚才已经投过票了，感谢您的参与！");
        }

        var b = await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.VoteUpCount, m => m.VoteUpCount + 1)) > 0;
        if (b)
        {
            HttpContext.Session.Set("post-vote" + id, id.GetBytes());
        }

        return ResultData(null, b, b ? "投票成功！" : "投票失败！");
    }

    /// <summary>
    /// 投稿页
    /// </summary>
    /// <returns></returns>
    public ActionResult Publish()
    {
        return View();
    }

    /// <summary>
    /// 发布投稿
    /// </summary>
    /// <param name="post"></param>
    /// <param name="code"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [HttpPost, ValidateAntiForgeryToken, DistributedLockFilter]
    public async Task<ActionResult> Publish([FromBodyOrDefault] PostCommand post, [Required(ErrorMessage = "验证码不能为空"), FromBodyOrDefault] string code, CancellationToken cancellationToken)
    {
        if (await RedisHelper.GetAsync("code:" + post.Email) != code)
        {
            return ResultData(null, false, "验证码错误！");
        }

        if (PostService.Any(p => p.Status == Status.Forbidden && p.Email == post.Email))
        {
            return ResultData(null, false, "由于您曾经恶意投稿，该邮箱已经被标记为黑名单，无法进行投稿，如有疑问，请联系网站管理员进行处理。");
        }

        var match = Regex.Match(post.Title + post.Author + post.Content, CommonHelper.BanRegex);
        if (match.Success)
        {
            LogManager.Info($"提交内容：{post.Title}/{post.Author}/{post.Content}，敏感词：{match.Value}");
            return ResultData(null, false, "您提交的内容包含敏感词，被禁止发表，请检查您的内容后尝试重新提交！");
        }

        if (!CategoryService.Any(c => c.Id == post.CategoryId))
        {
            return ResultData(null, message: "请选择一个分类");
        }

        post.Label = string.IsNullOrEmpty(post.Label?.Trim()) ? null : post.Label.Replace("，", ",");
        post.Status = Status.Pending;
        post.Content = await ImagebedClient.ReplaceImgSrc(await post.Content.HtmlSanitizerStandard().ClearImgAttributes(), cancellationToken);
        Post p = post.ToPost();
        p.IP = ClientIP.ToString();
        p.Modifier = p.Author;
        p.ModifierEmail = p.Email;
        p.DisableCopy = true;
        p.Rss = true;
        PostTagService.AddOrUpdate(t => t.Name, p.Label.AsNotNull().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new PostTag()
        {
            Name = s,
            Count = PostService.Count(t => t.Label.Contains(s))
        }));
        p = PostService.AddEntitySaved(p);
        if (p == null)
        {
            return ResultData(null, false, "文章发表失败！");
        }

        await RedisHelper.ExpireAsync("code:" + p.Email, 1);
        var content = new Template(await new FileInfo(HostEnvironment.WebRootPath + "/template/publish.html").ShareReadWrite().ReadAllTextAsync(Encoding.UTF8))
            .Set("link", Url.Action("Details", "Post", new { id = p.Id }, Request.Scheme))
            .Set("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Set("title", p.Title).Render();
        BackgroundJob.Enqueue<IMailSender>(sender => sender.Send(CommonHelper.SystemSettings["Title"] + "有访客投稿：", content, CommonHelper.SystemSettings["ReceiveEmail"], p.IP));
        return ResultData(p.ToDto(), message: "文章发表成功，待站长审核通过以后将显示到列表中！");
    }

    /// <summary>
    /// 获取标签
    /// </summary>
    /// <returns></returns>
    [ResponseCache(Duration = 600, VaryByHeader = "Cookie")]
    public ActionResult GetTag()
    {
        return ResultData(PostService.GetTags().Select(x => x.Key).OrderBy(s => s));
    }

    /// <summary>
    /// 标签云
    /// </summary>
    /// <returns></returns>
    [Route("all"), ResponseCache(Duration = 600, VaryByHeader = "Cookie")]
    public async Task<ActionResult> All()
    {
        ViewBag.tags = new Dictionary<string, int>(PostService.GetTags().Where(x => x.Value > 1).OrderBy(x => x.Key));
        ViewBag.cats = await CategoryService.GetQuery(c => c.Post.Count > 0, c => c.Post.Count, false).Include(c => c.Parent).ThenInclude(c => c.Parent).AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Path()); //category
        ViewBag.seminars = await SeminarService.GetAll(c => c.Post.Count, false).AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Title); //seminars
        return View();
    }

    /// <summary>
    /// 检查访问密码
    /// </summary>
    /// <param name="email"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    [HttpPost, ValidateAntiForgeryToken, AllowAccessFirewall, DistributedLockFilter]
    public ActionResult CheckViewToken(string email, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return ResultData(null, false, "请输入访问密码！");
        }

        var s = RedisHelper.Get("token:" + email);
        if (token.Equals(s))
        {
            HttpContext.Session.Set("AccessViewToken", token);
            Response.Cookies.Append("Email", email, new CookieOptions
            {
                Expires = DateTime.Now.AddYears(1),
                SameSite = SameSiteMode.Lax
            });
            Response.Cookies.Append("PostAccessToken", email.MDString3(AppConfig.BaiduAK), new CookieOptions
            {
                Expires = DateTime.Now.AddYears(1),
                SameSite = SameSiteMode.Lax
            });
            return ResultData(null);
        }

        return ResultData(null, false, "访问密码不正确！");
    }

    /// <summary>
    /// 检查授权邮箱
    /// </summary>
    /// <param name="email"></param>
    /// <returns></returns>
    [HttpPost, ValidateAntiForgeryToken, AllowAccessFirewall, DistributedLockFilter]
    public ActionResult GetViewToken(string email)
    {
        var validator = new IsEmailAttribute();
        if (!validator.IsValid(email))
        {
            return ResultData(null, false, validator.ErrorMessage);
        }

        if (RedisHelper.Exists("get:" + email))
        {
            RedisHelper.Expire("get:" + email, 120);
            return ResultData(null, false, "发送频率限制，请在2分钟后重新尝试发送邮件！请检查你的邮件，若未收到，请检查你的邮箱地址或邮件垃圾箱！");
        }

        if (!UserInfoService.Any(b => b.Email.Equals(email)))
        {
            return ResultData(null, false, "您目前没有权限访问这个链接，请联系站长开通访问权限！");
        }

        var token = SnowFlake.GetInstance().GetUniqueShortId(6);
        RedisHelper.Set("token:" + email, token, 86400);
        BackgroundJob.Enqueue<IMailSender>(sender => sender.Send(Request.Host + "博客访问验证码", $"{Request.Host}本次验证码是：<span style='color:red'>{token}</span>，有效期为24h，请按时使用！", email, ClientIP.ToString()));
        RedisHelper.Set("get:" + email, token, 120);
        return ResultData(null);
    }

    /// <summary>
    /// 文章合并
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("{id}/merge")]
    public async Task<ActionResult> PushMerge(int id)
    {
        var post = await PostService.GetAsync(p => p.Id == id && p.Status == Status.Published && !p.Locked) ?? throw new NotFoundException("文章未找到");
        CheckPermission(post);
        return View(post);
    }

    /// <summary>
    /// 文章合并
    /// </summary>
    /// <param name="messageService"></param>
    /// <param name="postMergeRequestService"></param>
    /// <param name="dto"></param>
    /// <returns></returns>
    [HttpPost("{id}/pushmerge"), DistributedLockFilter]
    public async Task<ActionResult> PushMerge([FromServices] IInternalMessageService messageService, [FromServices] IPostMergeRequestService postMergeRequestService, [FromBodyOrDefault] PostMergeRequestCommand dto)
    {
        if (await RedisHelper.GetAsync("code:" + dto.ModifierEmail) != dto.Code)
        {
            return ResultData(null, false, "验证码错误！");
        }

        var post = await PostService.GetAsync(p => p.Id == dto.PostId && p.Status == Status.Published && !p.Locked) ?? throw new NotFoundException("文章未找到");
        if (post.Title.Equals(dto.Title) && post.Content.HammingDistance(dto.Content) <= 1)
        {
            return ResultData(null, false, "内容未被修改或修改的内容过少(无意义修改)！");
        }

        #region 合并验证

        if (postMergeRequestService.Any(p => p.ModifierEmail == dto.ModifierEmail && p.MergeState == MergeStatus.Block))
        {
            return ResultData(null, false, "由于您曾经多次恶意修改文章，已经被标记为黑名单，无法修改任何文章，如有疑问，请联系网站管理员进行处理。");
        }

        if (post.PostMergeRequests.Any(p => p.ModifierEmail == dto.ModifierEmail && p.MergeState == MergeStatus.Pending))
        {
            return ResultData(null, false, "您已经提交过一次修改请求正在待处理，暂不能继续提交修改请求！");
        }

        #endregion 合并验证

        #region 直接合并

        if (post.Email.Equals(dto.ModifierEmail))
        {
            var history = post.ToHistoryVersion();
            dto.Update(post);
            post.PostHistoryVersion.Add(history);
            post.ModifyDate = DateTime.Now;
            return await PostService.SaveChangesAsync() > 0 ? ResultData(null, true, "你是文章原作者，无需审核，文章已自动更新并在首页展示！") : ResultData(null, false, "操作失败！");
        }

        #endregion 直接合并

        var merge = post.PostMergeRequests.FirstOrDefault(r => r.Id == dto.Id && r.MergeState != MergeStatus.Merged);
        if (merge != null)
        {
            dto.Update(merge);
            merge.SubmitTime = DateTime.Now;
            merge.MergeState = MergeStatus.Pending;
        }
        else
        {
            merge = dto.ToEntity();
            merge.SubmitTime = DateTime.Now;
            post.PostMergeRequests.Add(merge);
        }
        merge.IP = ClientIP.ToString();
        var b = await PostService.SaveChangesAsync() > 0;
        if (!b)
        {
            return ResultData(null, false, "操作失败！");
        }

        await RedisHelper.ExpireAsync("code:" + dto.ModifierEmail, 1);
        await messageService.AddEntitySavedAsync(new InternalMessage()
        {
            Title = $"来自【{dto.Modifier}】对文章《{post.Title}》的修改请求",
            Content = dto.Title,
            Link = "#/merge/compare?id=" + merge.Id
        });

        var diff = post.Content.RemoveHtmlTag().HtmlDiffMerge(dto.Content.RemoveHtmlTag());
        var content = new Template(await new FileInfo(HostEnvironment.WebRootPath + "/template/merge-request.html").ShareReadWrite().ReadAllTextAsync(Encoding.UTF8))
            .Set("title", post.Title)
            .Set("link", Url.Action("Index", "Dashboard", new { }, Request.Scheme) + "#/merge/compare?id=" + merge.Id)
            .Set("diff", diff)
            .Set("host", "//" + Request.Host)
            .Set("id", merge.Id.ToString())
            .Render();
        BackgroundJob.Enqueue<IMailSender>(sender => sender.Send("博客文章修改请求：", content, CommonHelper.SystemSettings["ReceiveEmail"], merge.IP));
        return ResultData(null, true, "您的修改请求已提交，已进入审核状态，感谢您的参与！");
    }

    /// <summary>
    /// 文章合并
    /// </summary>
    /// <param name="id"></param>
    /// <param name="mid"></param>
    /// <returns></returns>
    [HttpGet("{id}/merge/{mid}")]
    public async Task<ActionResult> RepushMerge(int id, int mid)
    {
        var post = await PostService.GetAsync(p => p.Id == id && p.Status == Status.Published && !p.Locked) ?? throw new NotFoundException("文章未找到");
        CheckPermission(post);
        var merge = post.PostMergeRequests.FirstOrDefault(p => p.Id == mid && p.MergeState != MergeStatus.Merged) ?? throw new NotFoundException("待合并文章未找到");
        return View(merge);
    }

    #region 后端管理

    /// <summary>
    /// 固顶
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Fixtop(int id)
    {
        Post post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.IsFixedTop = !post.IsFixedTop;
        bool b = await PostService.SaveChangesAsync() > 0;
        return b ? ResultData(null, true, post.IsFixedTop ? "置顶成功！" : "取消置顶成功！") : ResultData(null, false, "操作失败！");
    }

    /// <summary>
    /// 审核
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Pass(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.Status = Status.Published;
        post.ModifyDate = DateTime.Now;
        post.PostDate = DateTime.Now;
        var b = await PostService.SaveChangesAsync() > 0;
        if (!b)
        {
            return ResultData(null, false, "审核失败！");
        }

        (post.Keyword + "," + post.Label).Split(',', StringSplitOptions.RemoveEmptyEntries).ForEach(KeywordsManager.AddWords);
        SearchEngine.LuceneIndexer.Add(post);
        return ResultData(null, true, "审核通过！");
    }

    /// <summary>
    /// 下架文章
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Takedown(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.Status = Status.Takedown;
        bool b = await PostService.SaveChangesAsync(true) > 0;
        SearchEngine.LuceneIndexer.Delete(post);
        return ResultData(null, b, b ? $"文章《{post.Title}》已下架！" : "下架失败！");
    }

    /// <summary>
    /// 还原版本
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Takeup(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.Status = Status.Published;
        bool b = await PostService.SaveChangesAsync() > 0;
        SearchEngine.LuceneIndexer.Add(post);
        return ResultData(null, b, b ? "上架成功！" : "上架失败！");
    }

    /// <summary>
    /// 彻底删除文章
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public ActionResult Truncate(int id)
    {
        bool b = PostService - id;
        return ResultData(null, b, b ? "删除成功！" : "删除失败！");
    }

    /// <summary>
    /// 获取文章
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public ActionResult Get(int id)
    {
        var post = PostService.GetQuery(e => e.Id == id).Include(e => e.Seminar).FirstOrDefault() ?? throw new NotFoundException("文章未找到");
        var model = post.ToDto();
        model.Seminars = post.Seminar.Select(s => s.Id).Join(",");
        return ResultData(model);
    }

    /// <summary>
    /// 获取文章分页
    /// </summary>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> GetPageData([FromServices] IRedisClient cacheManager, int page = 1, [Range(1, 200, ErrorMessage = "页大小必须介于{1}-{2}")] int size = 10, OrderBy orderby = OrderBy.ModifyDate, string kw = "", int? cid = null, bool useRegex = false)
    {
        Expression<Func<Post, bool>> where = p => true;
        if (cid.HasValue)
        {
            where = where.And(p => p.CategoryId == cid.Value || p.Category.ParentId == cid.Value || p.Category.Parent.ParentId == cid.Value);
        }

        if (!string.IsNullOrEmpty(kw))
        {
            where = useRegex ? where.And(p => Regex.IsMatch(p.Title + p.Author + p.Email + p.Content + p.ProtectContent, kw, RegexOptions.IgnoreCase)) : where.And(p => (p.Title + p.Author + p.Email + p.Content + p.ProtectContent).Contains(kw));
        }

        var list = orderby switch
        {
            OrderBy.Trending => await PostService.GetQuery(where).OrderByDescending(p => p.Status).ThenByDescending(p => p.IsFixedTop).ThenByDescending(p => p.PostVisitRecordStats.Average(t => t.Count)).ProjectDataModel().ToPagedListAsync(page, size),
            _ => await PostService.GetQuery(where).OrderBy($"{nameof(Post.Status)} desc,{nameof(Post.IsFixedTop)} desc,{orderby.GetDisplay()} desc").ProjectDataModel().ToPagedListAsync(page, size)
        };
        foreach (var item in list.Data)
        {
            item.ModifyDate = item.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
            item.PostDate = item.PostDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
            item.Online = (int)await cacheManager.SCardAsync(nameof(PostOnline) + ":" + item.Id);
        }

        return Ok(list);
    }

    /// <summary>
    /// 获取未审核文章
    /// </summary>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <param name="search"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> GetPending([Range(1, int.MaxValue, ErrorMessage = "页码必须大于0")] int page = 1, [Range(1, 50, ErrorMessage = "页大小必须在0到50之间")] int size = 15, string search = "")
    {
        Expression<Func<Post, bool>> where = p => p.Status == Status.Pending;
        if (!string.IsNullOrEmpty(search))
        {
            where = where.And(p => p.Title.Contains(search) || p.Author.Contains(search) || p.Email.Contains(search) || p.Label.Contains(search));
        }

        var pages = await PostService.GetQuery(where).OrderByDescending(p => p.IsFixedTop).ThenByDescending(p => p.ModifyDate).ProjectDataModel().ToPagedListAsync(page, size);
        foreach (var item in pages.Data)
        {
            item.ModifyDate = item.ModifyDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
            item.PostDate = item.PostDate.ToTimeZone(HttpContext.Session.Get<string>(SessionKey.TimeZone));
        }

        return Ok(pages);
    }

    /// <summary>
    /// 编辑
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [HttpPost, MyAuthorize, DistributedLockFilter]
    public async Task<ActionResult> Edit([FromBodyOrDefault] PostCommand cmd, CancellationToken cancellationToken = default)
    {
        cmd.Content = await ImagebedClient.ReplaceImgSrc(await cmd.Content.Trim().ClearImgAttributes(), cancellationToken);
        if (!ValidatePost(cmd, out var resultData))
        {
            return resultData;
        }

        Post post = await PostService.GetByIdAsync(cmd.Id);
        if (cmd.Reserve && post.Status == Status.Published)
        {
            if (post.Content.HammingDistance(cmd.Content) > 0)
            {
                var history = post.ToHistoryVersion();
                history.PostId = post.Id;
                PostHistoryVersionService.AddEntity(history);
            }

            if (post.Title.HammingDistance(cmd.Title) > 10 && CommentService.Any(c => c.PostId == post.Id && c.ParentId == null))
            {
                CommentService.AddEntity(new Comment
                {
                    Status = Status.Published,
                    NickName = "系统自动评论",
                    Email = post.Email,
                    Content = $"<p style=\"color:red\">温馨提示：由于文章发生了重大更新，本条评论之前的所有评论仅作为原文《{post.Title}》的历史评论保留，不作为本文的最新评论参考，请知悉！了解更多信息，请查阅本文的历史修改记录。</p>",
                    PostId = post.Id,
                    CommentDate = DateTime.Now,
                    IsMaster = true,
                    IsAuthor = true,
                    IP = "127.0.0.1",
                    Location = "内网",
                    GroupTag = SnowFlake.NewId,
                    Path = SnowFlake.NewId,
                });
            }

            post.ModifyDate = DateTime.Now;
            var user = HttpContext.Session.Get<UserInfoDto>(SessionKey.UserInfo);
            cmd.Modifier = string.IsNullOrEmpty(cmd.Modifier) ? user.NickName : cmd.Modifier;
            cmd.ModifierEmail = string.IsNullOrEmpty(cmd.ModifierEmail) ? user.Email : cmd.ModifierEmail;
        }

        cmd.Update(post);
        post.IP = ClientIP.ToString();
        post.Seminar.Clear();
        if (!string.IsNullOrEmpty(cmd.Seminars))
        {
            var tmp = cmd.Seminars.Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().Select(int.Parse).ToArray();
            var seminars = SeminarService.GetQuery(s => tmp.Contains(s.Id)).ToPooledListScope();
            post.Seminar.AddRange(seminars);
        }

        (post.Keyword + "," + post.Label).Split(',', StringSplitOptions.RemoveEmptyEntries).ForEach(KeywordsManager.AddWords);
        PostTagService.AddOrUpdate(t => t.Name, post.Label.AsNotNull().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new PostTag()
        {
            Name = s,
            Count = PostService.Count(t => t.Label.Contains(s))
        }));
        bool b = await SearchEngine.SaveChangesAsync() > 0;
        if (!b)
        {
            return ResultData(null, false, "文章修改失败！");
        }

        if (post.LimitMode == RegionLimitMode.OnlyForSearchEngine)
        {
            SearchEngine.LuceneIndexer.Delete(post);
        }
        return ResultData(post.ToDto(), message: "文章修改成功！");
    }

    /// <summary>
    /// 发布
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="timespan"></param>
    /// <param name="schedule"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [MyAuthorize, HttpPost, DistributedLockFilter]
    public async Task<ActionResult> Write([FromBodyOrDefault] PostCommand cmd, [FromBodyOrDefault] DateTime? timespan, [FromBodyOrDefault] bool schedule = false, CancellationToken cancellationToken = default)
    {
        cmd.Content = await ImagebedClient.ReplaceImgSrc(await cmd.Content.Trim().ClearImgAttributes(), cancellationToken);
        if (!ValidatePost(cmd, out var resultData))
        {
            return resultData;
        }

        cmd.Status = Status.Published;
        Post post = cmd.ToPost();
        post.Modifier = post.Author;
        post.ModifierEmail = post.Email;
        post.IP = ClientIP.ToString();
        post.Rss = post.LimitMode is null or RegionLimitMode.All;
        if (!string.IsNullOrEmpty(cmd.Seminars))
        {
            var tmp = cmd.Seminars.Split(',').Distinct().Select(int.Parse).ToArray();
            post.Seminar.AddRange(SeminarService[s => tmp.Contains(s.Id)]);
        }

        if (schedule)
        {
            if (!timespan.HasValue || timespan.Value <= DateTime.Now)
            {
                return ResultData(null, false, "如果要定时发布，请选择正确的一个将来时间点！");
            }

            post.Status = Status.Schedule;
            post.PostDate = timespan.Value.ToUniversalTime();
            post.ModifyDate = timespan.Value.ToUniversalTime();
            BackgroundJob.Enqueue<IHangfireBackJob>(job => job.PublishPost(post));
            return ResultData(post.ToDto(), message: $"文章于{timespan.Value:yyyy-MM-dd HH:mm:ss}将会自动发表！");
        }

        PostService.AddEntity(post);
        (post.Keyword + "," + post.Label).Split(',', StringSplitOptions.RemoveEmptyEntries).ForEach(KeywordsManager.AddWords);
        PostTagService.AddOrUpdate(t => t.Name, post.Label.AsNotNull().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new PostTag()
        {
            Name = s,
            Count = PostService.Count(t => t.Label.Contains(s))
        }));
        bool b = await SearchEngine.SaveChangesAsync() > 0;
        if (!b)
        {
            return ResultData(null, false, "文章发表失败！");
        }

        if (post.LimitMode == RegionLimitMode.OnlyForSearchEngine)
        {
            SearchEngine.LuceneIndexer.Delete(post);
        }

        return ResultData(null, true, "文章发表成功！");
    }

    private bool ValidatePost(PostCommand post, out ActionResult resultData)
    {
        if (!CategoryService.Any(c => c.Id == post.CategoryId && c.Status == Status.Available))
        {
            resultData = ResultData(null, false, "请选择一个分类");
            return false;
        }

        switch (post.LimitMode)
        {
            case RegionLimitMode.AllowRegion:
            case RegionLimitMode.ForbidRegion:
                if (string.IsNullOrEmpty(post.Regions))
                {
                    resultData = ResultData(null, false, "请输入限制的地区");
                    return false;
                }

                post.Regions = post.Regions.Replace(",", "|").Replace("，", "|");
                break;

            case RegionLimitMode.AllowRegionExceptForbidRegion:
            case RegionLimitMode.ForbidRegionExceptAllowRegion:
                if (string.IsNullOrEmpty(post.ExceptRegions))
                {
                    resultData = ResultData(null, false, "请输入排除的地区");
                    return false;
                }

                post.ExceptRegions = post.ExceptRegions.Replace(",", "|").Replace("，", "|");
                goto case RegionLimitMode.AllowRegion;
        }

        if (string.IsNullOrEmpty(post.Label?.Trim()) || post.Label.Equals("null"))
        {
            post.Label = null;
        }
        else if (post.Label.Trim().Length > 50)
        {
            post.Label = post.Label.Replace("，", ",");
            post.Label = post.Label.Trim().Substring(0, 50);
        }
        else
        {
            post.Label = post.Label.Replace("，", ",");
        }

        if (string.IsNullOrEmpty(post.ProtectContent?.RemoveHtmlTag()) || post.ProtectContent.Equals("null"))
        {
            post.ProtectContent = null;
        }

        resultData = null;
        return true;
    }

    /// <summary>
    /// 添加专题
    /// </summary>
    /// <param name="id"></param>
    /// <param name="sid"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> AddSeminar(int id, int sid)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        Seminar seminar = await SeminarService.GetByIdAsync(sid) ?? throw new NotFoundException("专题未找到");
        post.Seminar.Add(seminar);
        bool b = await PostService.SaveChangesAsync() > 0;
        return ResultData(null, b, b ? $"已将文章【{post.Title}】添加到专题【{seminar.Title}】" : "添加失败");
    }

    /// <summary>
    /// 移除专题
    /// </summary>
    /// <param name="id"></param>
    /// <param name="sid"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> RemoveSeminar(int id, int sid)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        Seminar seminar = await SeminarService.GetByIdAsync(sid) ?? throw new NotFoundException("专题未找到");
        post.Seminar.Remove(seminar);
        bool b = await PostService.SaveChangesAsync() > 0;
        return ResultData(null, b, b ? $"已将文章【{post.Title}】从【{seminar.Title}】专题移除" : "添加失败");
    }

    /// <summary>
    /// 删除历史版本
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> DeleteHistory(int id)
    {
        bool b = await PostHistoryVersionService.DeleteByIdAsync(id) > 0;
        return ResultData(null, b, b ? "历史版本文章删除成功！" : "历史版本文章删除失败！");
    }

    /// <summary>
    /// 还原版本
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Revert(int id)
    {
        var history = await PostHistoryVersionService.GetByIdAsync(id) ?? throw new NotFoundException("版本不存在");
        history.Post.Category = history.Category;
        history.Post.CategoryId = history.CategoryId;
        history.Post.Content = history.Content;
        history.Post.Title = history.Title;
        history.Post.Label = history.Label;
        history.Post.ModifyDate = history.ModifyDate;
        history.Post.Seminar.Clear();
        foreach (var s in history.Seminar)
        {
            history.Post.Seminar.Add(s);
        }
        bool b = await SearchEngine.SaveChangesAsync() > 0;
        await PostHistoryVersionService.DeleteByIdAsync(id);
        return ResultData(null, b, b ? "回滚成功" : "回滚失败");
    }

    /// <summary>
    /// 禁用或开启文章评论
    /// </summary>
    /// <param name="id">文章id</param>
    /// <returns></returns>
    [MyAuthorize]
    [HttpPost("post/{id}/DisableComment"), DistributedLockFilter]
    public async Task<ActionResult> DisableComment(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.DisableComment = !post.DisableComment;
        return ResultData(null, await PostService.SaveChangesAsync() > 0, post.DisableComment ? $"已禁用【{post.Title}】这篇文章的评论功能！" : $"已启用【{post.Title}】这篇文章的评论功能！");
    }

    /// <summary>
    /// 禁用或开启文章评论
    /// </summary>
    /// <param name="id">文章id</param>
    /// <returns></returns>
    [MyAuthorize]
    [HttpPost("post/{id}/DisableCopy"), DistributedLockFilter]
    public async Task<ActionResult> DisableCopy(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.DisableCopy = !post.DisableCopy;
        return ResultData(null, await PostService.SaveChangesAsync() > 0, post.DisableCopy ? $"已开启【{post.Title}】这篇文章的防复制功能！" : $"已关闭【{post.Title}】这篇文章的防复制功能！");
    }

    /// <summary>
    /// 禁用或开启NSFW
    /// </summary>
    /// <param name="id">文章id</param>
    /// <returns></returns>
    [MyAuthorize]
    [HttpPost("post/{id}/nsfw"), DistributedLockFilter]
    public async Task<ActionResult> Nsfw(int id)
    {
        var post = await PostService.GetByIdAsync(id) ?? throw new NotFoundException("文章未找到");
        post.IsNsfw = !post.IsNsfw;
        return ResultData(null, await PostService.SaveChangesAsync() > 0, post.IsNsfw ? $"已将文章【{post.Title}】标记为不安全内容！" : $"已将文章【{post.Title}】取消标记为不安全内容！");
    }

    /// <summary>
    /// 修改分类
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cid"></param>
    /// <returns></returns>
    [HttpPost("post/{id}/ChangeCategory/{cid}"), DistributedLockFilter]
    public async Task<ActionResult> ChangeCategory(int id, int cid)
    {
        await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, cid));
        return Ok();
    }

    /// <summary>
    /// 修改专题
    /// </summary>
    /// <param name="id"></param>
    /// <param name="sids"></param>
    /// <returns></returns>
    [HttpPost("post/{id}/ChangeSeminar"), DistributedLockFilter]
    public async Task<ActionResult> ChangeSeminar(int id, string sids)
    {
        var post = await PostService.GetQuery(e => e.Id == id).Include(e => e.Seminar).FirstOrDefaultAsync() ?? throw new NotFoundException("文章不存在");
        post.Seminar.Clear();
        if (!string.IsNullOrEmpty(sids))
        {
            var ids = sids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            post.Seminar.AddRange(SeminarService[s => ids.Contains(s.Id)]);
        }

        await PostService.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// 刷新文章
    /// </summary>
    /// <param name="id">文章id</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<ActionResult> Refresh(int id, CancellationToken cancellationToken = default)
    {
        await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ModifyDate, DateTime.Now), cancellationToken: cancellationToken);
        return RedirectToAction("Details", new { id });
    }

    /// <summary>
    /// 标记为恶意修改
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [MyAuthorize]
    [HttpPost("post/block/{id}"), DistributedLockFilter]
    public async Task<ActionResult> Block(int id, CancellationToken cancellationToken = default)
    {
        var b = await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Status.Forbidden), cancellationToken: cancellationToken) > 0;
        return b ? ResultData(null, true, "操作成功！") : ResultData(null, false, "操作失败！");
    }

    /// <summary>
    /// 切换允许rss订阅
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [MyAuthorize, DistributedLockFilter]
    [HttpPost("post/{id}/rss-switch")]
    public async Task<ActionResult> RssSwitch(int id, CancellationToken cancellationToken = default)
    {
        await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Rss, p => !p.Rss), cancellationToken: cancellationToken);
        return ResultData(null, message: "操作成功");
    }

    /// <summary>
    /// 切换锁定编辑
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [MyAuthorize, DistributedLockFilter]
    [HttpPost("post/{id}/locked-switch")]
    public async Task<ActionResult> LockedSwitch(int id, CancellationToken cancellationToken = default)
    {
        await PostService.GetQuery(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Locked, p => !p.Locked), cancellationToken: cancellationToken);
        return ResultData(null, message: "操作成功");
    }

    /// <summary>
    /// 文章统计
    /// </summary>
    /// <returns></returns>
    [MyAuthorize]
    public async Task<IActionResult> Statistic(CancellationToken cancellationToken = default)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Add("X-Accel-Buffering", "no");
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            await Response.WriteAsync($"event: message\n", cancellationToken);
            var keys = await RedisHelper.KeysAsync(nameof(PostOnline) + ":*");
            var sets = keys.Select(s => (Id: s.Split(':')[1].ToInt32(), Clients: RedisHelper.SMembers(s))).ToArray();
            var ids = sets.OrderByDescending(t => t.Clients.Length).Take(10).Select(t => t.Id).ToArray();
            var mostHots = await PostService.GetQuery(p => ids.Contains(p.Id)).ProjectModelBase().ToListWithNoLockAsync(cancellationToken).ContinueWith(t =>
            {
                foreach (var item in t.Result)
                {
                    item.ViewCount = sets.FirstOrDefault(x => x.Id == item.Id).Clients.Length;
                }

                return t.Result.OrderByDescending(p => p.ViewCount);
            });
            var postsQuery = PostService.GetQuery(p => p.Status == Status.Published);
            var mostView = await postsQuery.OrderByDescending(p => p.TotalViewCount).Take(10).Select(p => new PostModelBase()
            {
                Id = p.Id,
                Title = p.Title,
                ViewCount = p.TotalViewCount
            }).ToListWithNoLockAsync(cancellationToken);
            var mostAverage = await postsQuery.OrderByDescending(p => p.AverageViewCount).Take(10).Select(p => new PostModelBase()
            {
                Id = p.Id,
                Title = p.Title,
                ViewCount = (int)p.AverageViewCount
            }).ToListWithNoLockAsync(cancellationToken);
            var yesterday = DateTime.Now.AddDays(-1);
            var trending = await postsQuery.Select(p => new PostModelBase()
            {
                Id = p.Id,
                Title = p.Title,
                ViewCount = p.PostVisitRecords.Count(t => t.Time >= yesterday)
            }).OrderByDescending(p => p.ViewCount).Take(10).ToListWithNoLockAsync(cancellationToken);
            var readCount = PostVisitRecordService.Count(e => e.Time >= yesterday);
            await Response.WriteAsync("data:" + new
            {
                mostHots,
                mostView,
                mostAverage,
                trending,
                readCount
            }.ToJsonString() + "\r\r");
            await Response.Body.FlushAsync(cancellationToken);
            await Task.Delay(5000, cancellationToken);
        }

        Response.Body.Close();
        return Ok();
    }

    /// <summary>
    /// 文章访问记录
    /// </summary>
    /// <param name="id"></param>
    /// <param name="page"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    [HttpGet("/{id}/records"), MyAuthorize]
    [ProducesResponseType(typeof(PagedList<PostVisitRecordViewModel>), (int)HttpStatusCode.OK)]
    public async Task<IActionResult> PostVisitRecords(int id, int page = 1, int size = 15, string kw = "")
    {
        Expression<Func<PostVisitRecord, bool>> where = e => e.PostId == id;
        if (!string.IsNullOrEmpty(kw))
        {
            kw = Regex.Escape(kw);
            where = where.And(e => Regex.IsMatch(e.IP + e.Location + e.Referer + e.RequestUrl, kw, RegexOptions.IgnoreCase));
        }

        var pages = await PostVisitRecordService.GetQuery(where, e => e.Time, false).ProjectViewModel().ToPagedListNoLockAsync(page, size);
        return Ok(pages);
    }

    /// <summary>
    /// 导出文章访问记录
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("/{id}/records-export"), MyAuthorize]
    [ProducesResponseType(typeof(PagedList<PostVisitRecordViewModel>), (int)HttpStatusCode.OK)]
    public IActionResult ExportPostVisitRecords(int id)
    {
        var list = PostVisitRecordService.GetQuery(e => e.PostId == id, e => e.Time, false).ProjectViewModel().ToPooledListScope();
        using var ms = list.ToExcel();
        var post = PostService[id];
        return this.ResumeFile(ms.ToArray(), ContentType.Xlsx, post.Title + "访问记录.xlsx");
    }

    /// <summary>
    /// 文章访问记录图表
    /// </summary>
    /// <returns></returns>
    [HttpGet("/{id}/records-chart"), MyAuthorize]
    [ProducesResponseType((int)HttpStatusCode.OK)]
    public async Task<IActionResult> PostVisitRecordChart([FromServices] IPostVisitRecordStatsService statsService, int id, bool compare, uint period, CancellationToken cancellationToken)
    {
        if (compare)
        {
            var start1 = DateTime.Today.AddDays(-period);
            var list1 = await statsService.GetQuery(e => e.PostId == id && e.Date >= start1).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Sum(t => t.Count),
                UV = g.Sum(t => t.UV)
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
            if (list1.Count == 0)
            {
                return Ok(Array.Empty<int>());
            }

            var start2 = start1.AddDays(-period - 1);
            var list2 = await statsService.GetQuery(e => e.PostId == id && e.Date >= start2 && e.Date < start1).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Sum(t => t.Count),
                UV = g.Sum(t => t.UV)
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);

            // 将数据填充成连续的数据
            for (var i = start1; i <= DateTime.Today; i = i.AddDays(1))
            {
                if (list1.TrueForAll(a => a.Date != i))
                {
                    list1.Add(new { Date = i, Count = 0, UV = 0 });
                }
            }
            for (var i = start2; i < start1; i = i.AddDays(1))
            {
                if (list2.TrueForAll(a => a.Date != i))
                {
                    list2.Add(new { Date = i, Count = 0, UV = 0 });
                }
            }
            return Ok(new[] { list1.OrderBy(a => a.Date), list2.OrderBy(a => a.Date) });
        }

        var list = await statsService.GetQuery(e => e.PostId == id).GroupBy(t => t.Date).Select(g => new
        {
            Date = g.Key,
            Count = g.Sum(t => t.Count),
            UV = g.Sum(t => t.UV)
        }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
        var min = list.Min(a => a.Date);
        var max = list.Max(a => a.Date);
        for (var i = min; i < max; i = i.AddDays(1))
        {
            if (list.TrueForAll(a => a.Date != i))
            {
                list.Add(new { Date = i, Count = 0, UV = 0 });
            }
        }

        return Ok(new[] { list.OrderBy(a => a.Date) });
    }

    /// <summary>
    /// 文章访问记录图表
    /// </summary>
    /// <returns></returns>
    [HttpGet("/post/records-chart"), MyAuthorize]
    [ProducesResponseType((int)HttpStatusCode.OK)]
    public async Task<IActionResult> PostVisitRecordChart(bool compare, uint period, CancellationToken cancellationToken)
    {
        if (compare)
        {
            var start1 = DateTime.Today.AddDays(-period);
            var list1 = await PostVisitRecordService.GetQuery(e => e.Time >= start1).Select(e => new { e.Time.Date, e.IP }).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count(),
                UV = g.Select(e => e.IP).Distinct().Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
            if (list1.Count == 0)
            {
                return Ok(Array.Empty<int>());
            }

            var start2 = start1.AddDays(-period - 1);
            var list2 = await PostVisitRecordService.GetQuery(e => e.Time >= start2 && e.Time < start1).Select(e => new { e.Time.Date, e.IP }).GroupBy(t => t.Date).Select(g => new
            {
                Date = g.Key,
                Count = g.Count(),
                UV = g.Select(e => e.IP).Distinct().Count()
            }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);

            // 将数据填充成连续的数据
            for (var i = start1; i <= DateTime.Today; i = i.AddDays(1))
            {
                if (list1.TrueForAll(a => a.Date != i))
                {
                    list1.Add(new { Date = i, Count = 0, UV = 0 });
                }
            }
            for (var i = start2; i < start1; i = i.AddDays(1))
            {
                if (list2.TrueForAll(a => a.Date != i))
                {
                    list2.Add(new { Date = i, Count = 0, UV = 0 });
                }
            }
            return Ok(new[] { list1.OrderBy(a => a.Date), list2.OrderBy(a => a.Date) });
        }

        var list = await PostVisitRecordService.GetAll().Select(e => new { e.Time.Date, e.IP }).GroupBy(t => t.Date).Select(g => new
        {
            Date = g.Key,
            Count = g.Count(),
            UV = g.Select(e => e.IP).Distinct().Count()
        }).OrderBy(a => a.Date).ToListWithNoLockAsync(cancellationToken);
        var min = list.Min(a => a.Date);
        var max = list.Max(a => a.Date);
        for (var i = min; i < max; i = i.AddDays(1))
        {
            if (list.TrueForAll(a => a.Date != i))
            {
                list.Add(new { Date = i, Count = 0, UV = 0 });
            }
        }

        return Ok(new[] { list.OrderBy(a => a.Date) });
    }

    /// <summary>
    /// 文章访问记录分析
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("/{id}/insight"), MyAuthorize]
    [ProducesResponseType(typeof(PagedList<PostVisitRecordViewModel>), (int)HttpStatusCode.OK)]
    public IActionResult PostVisitRecordInsight(int id)
    {
        return View(PostService[id]);
    }

    /// <summary>
    /// 获取地区集
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    [MyAuthorize]
    [ProducesResponseType(typeof(List<string>), (int)HttpStatusCode.OK)]
    public async Task<IActionResult> GetRegions(string name)
    {
        return ResultData(await PostService.GetAll().Select(p => EF.Property<string>(p, name)).Distinct().ToListWithNoLockAsync());
    }

    #endregion 后端管理
}