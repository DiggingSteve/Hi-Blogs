﻿using Hiblogs.Redis;
using HiBlogs.Application.Admin;
using HiBlogs.Core.Entities;
using HiBlogs.Definitions;
using HiBlogs.Definitions.Config;
using HiBlogs.Definitions.HiClaimTypes;
using HiBlogs.EntityFramework.EntityFramework;
using HiBlogs.Infrastructure;
using HiBlogs.Infrastructure.Models;
using HiBlogs.WebApi.Api.Controllers.Dto;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using Talk.OAuthClient;

namespace HiBlogs.WebApi.Api.Controllers
{

    [Route("api/[controller]/[Action]")]
    public class AccountController : Controller
    {
        private readonly SignInManager<User> signInManager;
        private readonly HiBlogsDbContext dbContext;
        private readonly UserManager<User> userManager;
        private readonly RoleManager<Role> roleManager;
        private readonly AccountAppService accountAppService;

        public AccountController(SignInManager<User> signInManager,
            HiBlogsDbContext hiBlogsDbContext,
            UserManager<User> userManager,
            RoleManager<Role> roleManager)
        {
            this.signInManager = signInManager;
            this.userManager = userManager;
            dbContext = hiBlogsDbContext;
            this.roleManager = roleManager;
            accountAppService = new AccountAppService();
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="userName">用户名</param>
        /// <param name="passwod">密码</param>
        /// <param name="rememberMe">是否记住密码</param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ReturnResult> Login(string userName, string passwod, bool rememberMe, string returnUrl = null)
        {
            var returnResult = new ReturnResult();
            var user = await userManager.Users.Where(t => t.UserName == userName).FirstOrDefaultAsync();

            if (user == null)
            {
                returnResult.IsSuccess = false;
                returnResult.Description = "不存在此用户";
                return returnResult;
            }
            var result = await signInManager.PasswordSignInAsync(user, passwod, rememberMe, lockoutOnFailure: false);
            if (result.Succeeded)
            {
                var userPrincipal = await signInManager.CreateUserPrincipalAsync(user);
                userPrincipal.Identities.First().AddClaim(new Claim(HiClaimTypes.Nickname, user.Nickname ?? ""));
                await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(userPrincipal), new Microsoft.AspNetCore.Authentication.AuthenticationProperties());
                returnResult.ReturnUrl = returnUrl;
                return returnResult;
            }
            returnResult.IsSuccess = false;
            returnResult.Description = "登录失败";
            return returnResult;
        }

        /// <summary>
        /// 初始化数据
        /// </summary>
        /// <returns></returns>
        public async Task InitData()
        {
            if (!dbContext.Users.Any())
            {
                var roleAdministrator = new Role { Name = RoleNames.Administrator };
                var roleAdmin = new Role { Name = RoleNames.Admin };
                var roleAverage = new Role { Name = RoleNames.Average };
                await roleManager.CreateAsync(roleAdministrator);
                await roleManager.CreateAsync(roleAdmin);
                await roleManager.CreateAsync(roleAverage);
                var user = new User { UserName = "Administrator", Email = "Administrator@haojima.net" };
                var createUserResult = await userManager.CreateAsync(user, "123qwe");
                var addRoleResult = await userManager.AddToRoleAsync(user, RoleNames.Administrator);
            }
        }

        /// <summary>
        /// 获取社交帐号认证地址[QQ]
        /// </summary>
        /// <returns></returns>
        public string GetOAuthQQUrl()
        {
            return accountAppService.GetOAuthClient(AuthType.QQ, Request).GetAuthUrl();
        }
        /// <summary>
        /// 获取社交帐号认证地址[新浪]
        /// </summary>
        /// <returns></returns>
        public string GetOAuthSinaUrl()
        {
            return accountAppService.GetOAuthClient(AuthType.Sina, Request).GetAuthUrl();
        }

        /// <summary>
        /// 获取社交登录用户信息
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public async Task<AccessTokenObject> GetOAuthUser(string code, string type)
        {
            AuthType authType = AuthType.QQ;
            switch (type)
            {
                case "qq":
                    authType = AuthType.QQ;
                    break;
                case "sina":
                    authType = AuthType.Sina;
                    break;
            }
            var client = accountAppService.GetOAuthClient(authType, Request);
            var accessToken = await client.GetAccessToken(code);

            var user = await dbContext.Users.Where(t => t.OpenId == accessToken.UserId).FirstOrDefaultAsync();
            if (user == null)
            {
                var accessUser = await client.GetUserInfo(accessToken);
                user = new User
                {
                    UserName = DateTime.Now.Ticks.ToString(),
                    OpenId = accessToken.UserId,
                    Email = DateTime.Now.Ticks.ToString() + "@temp.com",
                    Nickname = accessUser.Name
                };
                var temp = await userManager.CreateAsync(user, accessUser.Id.ToLower() + "temp");
                await userManager.AddToRoleAsync(user, RoleNames.Average);
            }
            await signInManager.SignInAsync(user, true);
            var userPrincipal = await signInManager.CreateUserPrincipalAsync(user);
            userPrincipal.Identities.First().AddClaim(new Claim(HiClaimTypes.Nickname, user.Nickname ?? ""));
            await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(userPrincipal), new AuthenticationProperties());

            return accessToken;
        }

        /// <summary>
        /// 退出登录
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public async Task LogOff()
        {
            await signInManager.SignOutAsync();
        }

        /// <summary>
        /// 注册
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="passwod"></param>
        /// <param name="email"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ReturnResult> Register(string userName, string passwod, string email)
        {
            var result = new ReturnResult();
            var hasUser = await userManager.Users.Where(t => t.UserName == userName).AnyAsync();
            if (hasUser)
            {
                result.IsSuccess = false;
                result.Description = "已存在此用户";
                return result;
            }

            var user = new User { UserName = userName, Email = email };
            var data = JsonConvert.SerializeObject(new RegisterInfo() { User = user, Passwod = passwod });
            var DESString = HttpUtility.UrlEncode(EncryptDecryptExtension.DES3Encrypt(data, AppConfig.DESKey));
            var key = email;
            var number = await RedisHelper.GetStringIncrAsync(key);
            if (number >= 3)
            {
                result.IsSuccess = false;
                result.Description = "请勿频繁注册，请查看垃圾邮件或换一个邮箱注册！";
                Log.Warning("邮箱" + email + "连续注册" + number + "次");
                return result;
            }
            //30分钟内有效(标记邮件激活30分钟内有效)
            await RedisHelper.SetStringIncrAsync(key, TimeSpan.FromMinutes(30));

            var checkUrl = Request.Scheme + "://" + Request.Host.Value + "/Admin/Account/Activation?desstring=" + DESString;
            EmailHelper emailHelper = new EmailHelper()
            {
                mailPwd = EmailConfig.Passwod,
                host = EmailConfig.Host,
                mailFrom = EmailConfig.From,
                mailSubject = "欢迎您注册 嗨-博客",
                mailBody = EmailHelper.tempBody(userName, "请复制打开链接(或者右键新标签中打开)，激活账号。", "<a style='word-wrap: break-word;word-break: break-all;' href='" + checkUrl + "'>" + checkUrl + "</a>"),
                mailToArray = new string[] { email }
            };
            //发送邮件
            emailHelper.Send(t =>
            {
                Log.Information("邮件发送成功");
            }, t =>
            {
                Log.Information("邮件发送失败");
            });
            return result;
        }

        /// <summary>
        /// 注册或忘记密码 根据链接验证注册信息（发送过去的邮件）
        /// </summary>
        [HttpPost]
        public async Task<ReturnResult> CheckUserInfo(string desstring)
        {
            var result = new ReturnResult();
            var jsonString = string.Empty;
            try
            {
                //这里有点妖啊。
                //如果是url直接跳转过来的就不需要HttpUtility.UrlDecode
                //如果是ajax异步传过来的就需要HttpUtility.UrlDecode
                jsonString = EncryptDecryptExtension.DES3Decrypt(HttpUtility.UrlDecode(desstring), AppConfig.DESKey);
            }
            catch (Exception)
            {
                jsonString = EncryptDecryptExtension.DES3Decrypt(desstring, AppConfig.DESKey);
            }
            var registerInfo = JsonConvert.DeserializeObject<RegisterInfo>(jsonString);
            if (!await RedisHelper.KeyExistsAsync(registerInfo.User.Email, RedisTypePrefix.String))
            {
                result.IsSuccess = false;
                result.Description = "激活链接已失效";
                return result;//
            }
            var user = await userManager.Users.Where(t => t.Email == registerInfo.User.Email).FirstOrDefaultAsync();
            if (user != null)//修改密码
            {
            }
            else//新增用户
            {
                user = registerInfo.User;
                //http://patrickdesjardins.com/blog/createidentityasync-value-cannot-be-null-when-logging-with-user-created-with-migration-tool
                user.SecurityStamp = Guid.NewGuid().ToString();//不知道SecurityStamp为什么偶尔报错？？
                await userManager.CreateAsync(user, registerInfo.Passwod);
                await userManager.AddToRoleAsync(user, RoleNames.Average);
            }
            await signInManager.SignInAsync(user, true);//登录
            await RedisHelper.DeleteKeyAsync(registerInfo.User.Email, RedisTypePrefix.String);//删除缓存，使验证过的邮件失效
            return result;
        }
    }
}
