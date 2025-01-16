﻿using Backend_Library.Data;
using Backend_Library.Helpers;
using Backend_Library.Repositories.Contracts;
using BaseLibrary.DTOs;
using BaseLibrary.Entities;
using BaseLibrary.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection.Metadata;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;


namespace Backend_Library.Repositories.Implementations
{
    public class UserAccountRepository(IOptions<JwtSection> config, AppDbContext appDbContext) : IUserAccount
    {
        public async Task<GeneralResponse> CreateAsync(Register user)
        {
            if (user == null) return new GeneralResponse(false, "Model trống");
            var checkUser = await FindUserByEmail(user.Email);
            if (checkUser != null) return new GeneralResponse(false, "Email đã tồn tại");

            //Lưu User 
            var applicationUser = await AddToDatabase(new ApplicationUser()
            {
                Email = user.Email,
                FullName = user.Fullname,
                Password = BCrypt.Net.BCrypt.HashPassword(user.Password)
            });
            //Kiểm  tra, tạo và ủy quyền cho user
            var checkAdminRole = await appDbContext.SystemRoles.FirstOrDefaultAsync(m => m.Name!.Equals(Constants.Admin));
            if (checkAdminRole is null)
            {
                var createAdminRole = await AddToDatabase(new SystemRole() { Name = Constants.Admin });
                await AddToDatabase(new UserRole() { RoleId = createAdminRole.Id, UserId = applicationUser.Id });
                return new GeneralResponse(true, "Tạo tài khoản Admin thành công");
            }

            var checkUserRole = await appDbContext.SystemRoles.FirstOrDefaultAsync(m => m.Name!.Equals(Constants.User));
            SystemRole response = new();
            if (checkUserRole is null)
            {
                var createUserRole = await AddToDatabase(new SystemRole() { Name = Constants.User });
                await AddToDatabase(new UserRole() { RoleId = response.Id, UserId = applicationUser.Id });
            }
            else
            {
                await AddToDatabase(new UserRole() { RoleId = checkUserRole.Id, UserId = applicationUser.Id });
            }
            return new GeneralResponse(true, "Tạo tài khoản thành công");

        }

        public async Task<LoginResponse> SingInAsync(Login user)
        {
            if (user is null) return new LoginResponse(false, "Model trống");
            var applicationUser = await FindUserByEmail(user.Email!);
            if (applicationUser is null) return new LoginResponse(false, "Không tìm thấy User");
            //Xác thực mật khẩu
            if (!BCrypt.Net.BCrypt.Verify(user.Password, applicationUser.Password))
                return new LoginResponse(false, "Email/Mật khẩu không chính xác");

            var getUserRole = await FindUserRole(applicationUser.Id);
            if (getUserRole is null) return new LoginResponse(false, "Không tìm thấy quyền của User");

            var getUserName = await FindRoleName(getUserRole.RoleId);
            if (getUserRole is null) return new LoginResponse(false, "Không tìm thấy quyền của User");

            string jwtToken = GenerateToken(applicationUser, getUserName!.Name!);
            string refreshToken = GenerateRefreshToken();

            //Lưu refresh token vào database
            var findUser = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(m => m.UserId == applicationUser.Id);
            if (findUser is not null)
            {
                findUser!.Token = refreshToken;
                await appDbContext.SaveChangesAsync();
            }
            else
            {
               await AddToDatabase(new RefreshTokenInfo() { Token = refreshToken, UserId = applicationUser.Id });
            }
                 return new LoginResponse(true, "Đăng nhập thành công", jwtToken, refreshToken);
        }
        //Phương thức generate token
        private string GenerateToken(ApplicationUser user, string role)
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.Value.Key!));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
            var usercClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.FullName!),
                new Claim(ClaimTypes.Email, user.Email!),
                new Claim(ClaimTypes.Role, role!)
            };
            var token = new JwtSecurityToken(
               issuer: config.Value.Issuer,
              audience: config.Value.Audience,
              claims: usercClaims,
                expires: DateTime.Now.AddDays(1),
                signingCredentials: credentials
                );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }


        private async Task<UserRole> FindUserRole(int userId) => await appDbContext.UserRoles.FirstOrDefaultAsync(m => m.UserId == userId);
        private async Task<SystemRole> FindRoleName(int roleId) => await appDbContext.SystemRoles.FirstOrDefaultAsync(n => n.Id == roleId);
        private static string GenerateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        private async Task<ApplicationUser> FindUserByEmail(string email)
        {
            return await appDbContext.ApplicationUsers.FirstOrDefaultAsync(x => x.Email!.ToLower().Equals(email!.ToLower()));
        }

        private async Task<T> AddToDatabase<T>(T model)
        {
            var result = appDbContext.Add(model!);
            await appDbContext.SaveChangesAsync();
            return (T)result.Entity;
        }

        public async Task<LoginResponse> RefreshTokenAsync(RefreshToken token)
        {
            if (token is null) return new LoginResponse(false, "Token trống");

            var findToken = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(m => m.Token!.Equals(token.Token));
            if (findToken is null) return new LoginResponse(false, "Yêu cầu phải có refresh token");

            //Lấy thông tin user
            var user = await appDbContext.ApplicationUsers.FirstOrDefaultAsync(m => m.Id == findToken.UserId);
            if (user is null) return new LoginResponse(false, "Không thể làm mới Token vì không tìm thấy User");

            var userRole = await FindUserRole(user.Id);
            var roleName = await FindRoleName(userRole.RoleId);
            string jwtToken = GenerateToken(user, roleName.Name!);
            string refreshToken = GenerateRefreshToken();

            var updateRefreshToken = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(m => m.UserId == user.Id);
            if (updateRefreshToken is null) return new LoginResponse(false, "Không thể làm mới Token vì User chưa đăng nhập");

            updateRefreshToken.Token = refreshToken;
            await appDbContext.SaveChangesAsync();
            return new LoginResponse(true, "Token đã được Refresh thành công", jwtToken, refreshToken);
        }
    }
}
