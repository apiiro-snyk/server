﻿using Bit.Core.AdminConsole.Entities;
using Bit.Core.AdminConsole.Enums;
using Bit.Core.AdminConsole.Repositories;
using Bit.Core.Auth.Enums;
using Bit.Core.Auth.Models;
using Bit.Core.Auth.Models.Business.Tokenables;
using Bit.Core.Auth.UserFeatures.Registration.Implementations;
using Bit.Core.Entities;
using Bit.Core.Exceptions;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Tokens;
using Bit.Core.Tools.Enums;
using Bit.Core.Tools.Models.Business;
using Bit.Core.Tools.Services;
using Bit.Core.Utilities;
using Bit.Test.Common.AutoFixture;
using Bit.Test.Common.AutoFixture.Attributes;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Xunit;

namespace Bit.Core.Test.Auth.UserFeatures.Registration;

[SutProviderCustomize]
public class RegisterUserCommandTests
{

    // RegisterUser tests
    [Theory]
    [BitAutoData]
    public async Task RegisterUser_Succeeds(SutProvider<RegisterUserCommand> sutProvider, User user)
    {
        // Arrange
        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user)
            .Returns(IdentityResult.Success);

        // Act
        var result = await sutProvider.Sut.RegisterUser(user);

        // Assert
        Assert.True(result.Succeeded);

        await sutProvider.GetDependency<IUserService>()
            .Received(1)
            .CreateUserAsync(user);

        await sutProvider.GetDependency<IMailService>()
            .Received(1)
            .SendWelcomeEmailAsync(user);

        await sutProvider.GetDependency<IReferenceEventService>()
            .Received(1)
            .RaiseEventAsync(Arg.Is<ReferenceEvent>(refEvent => refEvent.Type == ReferenceEventType.Signup));
    }

    [Theory]
    [BitAutoData]
    public async Task RegisterUser_WhenCreateUserFails_ReturnsIdentityResultFailed(SutProvider<RegisterUserCommand> sutProvider, User user)
    {
        // Arrange
        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user)
            .Returns(IdentityResult.Failed());

        // Act
        var result = await sutProvider.Sut.RegisterUser(user);

        // Assert
        Assert.False(result.Succeeded);

        await sutProvider.GetDependency<IUserService>()
            .Received(1)
            .CreateUserAsync(user);

        await sutProvider.GetDependency<IMailService>()
            .DidNotReceive()
            .SendWelcomeEmailAsync(Arg.Any<User>());

        await sutProvider.GetDependency<IReferenceEventService>()
            .DidNotReceive()
            .RaiseEventAsync(Arg.Any<ReferenceEvent>());
    }

    // RegisterUserWithOptionalOrgInvite tests

    // Simple happy path test
    [Theory]
    [BitAutoData]
    public async Task RegisterUserWithOptionalOrgInvite_NoOrgInviteOrOrgUserIdOrReferenceData_Succeeds(
        SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash)
    {
        // Arrange
        user.ReferenceData = null;

        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user, masterPasswordHash)
            .Returns(IdentityResult.Success);

        // Act
        var result = await sutProvider.Sut.RegisterUserWithOptionalOrgInvite(user, masterPasswordHash, null, null);

        // Assert
        Assert.True(result.Succeeded);

        await sutProvider.GetDependency<IUserService>()
            .Received(1)
            .CreateUserAsync(user, masterPasswordHash);

        await sutProvider.GetDependency<IReferenceEventService>()
            .Received(1)
            .RaiseEventAsync(Arg.Is<ReferenceEvent>(refEvent => refEvent.Type == ReferenceEventType.Signup));
    }

    // Complex happy path test
    [Theory]
    [BitAutoData(false, null)]
    [BitAutoData(true, "sampleInitiationPath")]
    [BitAutoData(true, "Secrets Manager trial")]
    public async Task RegisterUserWithOptionalOrgInvite_ComplexHappyPath_Succeeds(bool addUserReferenceData, string initiationPath,
        SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash, OrganizationUser orgUser, string orgInviteToken, Guid orgUserId, Policy twoFactorPolicy)
    {
        // Arrange
        sutProvider.GetDependency<IGlobalSettings>()
            .DisableUserRegistration.Returns(false);

        sutProvider.GetDependency<IGlobalSettings>()
            .DisableUserRegistration.Returns(true);

        orgUser.Email = user.Email;
        orgUser.Id = orgUserId;

        var orgInviteTokenable = new OrgUserInviteTokenable(orgUser);

        sutProvider.GetDependency<IDataProtectorTokenFactory<OrgUserInviteTokenable>>()
            .TryUnprotect(orgInviteToken, out Arg.Any<OrgUserInviteTokenable>())
            .Returns(callInfo =>
            {
                callInfo[1] = orgInviteTokenable;
                return true;
            });

        sutProvider.GetDependency<IOrganizationUserRepository>()
            .GetByIdAsync(orgUserId)
            .Returns(orgUser);

        twoFactorPolicy.Enabled = true;
        sutProvider.GetDependency<IPolicyRepository>()
            .GetByOrganizationIdTypeAsync(orgUser.OrganizationId, PolicyType.TwoFactorAuthentication)
            .Returns(twoFactorPolicy);

        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user, masterPasswordHash)
            .Returns(IdentityResult.Success);

        user.ReferenceData = addUserReferenceData ? $"{{\"initiationPath\":\"{initiationPath}\"}}" : null;

        // Act
        var result = await sutProvider.Sut.RegisterUserWithOptionalOrgInvite(user, masterPasswordHash, orgInviteToken, orgUserId);

        // Assert
        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .GetByIdAsync(orgUserId);

        await sutProvider.GetDependency<IPolicyRepository>()
            .Received(1)
            .GetByOrganizationIdTypeAsync(orgUser.OrganizationId, PolicyType.TwoFactorAuthentication);

        sutProvider.GetDependency<IUserService>()
            .Received(1)
            .SetTwoFactorProvider(user, TwoFactorProviderType.Email);

        // example serialized data: {"1":{"Enabled":true,"MetaData":{"Email":"0dbf746c-deaf-4318-811e-d98ea7155075"}}}
        var twoFactorProviders = new Dictionary<TwoFactorProviderType, TwoFactorProvider>
        {
            [TwoFactorProviderType.Email] = new TwoFactorProvider
            {
                MetaData = new Dictionary<string, object> { ["Email"] = user.Email.ToLowerInvariant() },
                Enabled = true
            }
        };

        var serializedTwoFactorProviders =
            JsonHelpers.LegacySerialize(twoFactorProviders, JsonHelpers.LegacyEnumKeyResolver);

        Assert.Equal(user.TwoFactorProviders, serializedTwoFactorProviders);

        await sutProvider.GetDependency<IUserService>()
            .Received(1)
            .CreateUserAsync(user, masterPasswordHash);

        if (addUserReferenceData)
        {
            if (initiationPath.Contains("Secrets Manager trial"))
            {
                await sutProvider.GetDependency<IMailService>()
                    .Received(1)
                    .SendTrialInitiationEmailAsync(user.Email);
            }
            else
            {
                await sutProvider.GetDependency<IMailService>()
                    .Received(1)
                    .SendWelcomeEmailAsync(user);
            }

            await sutProvider.GetDependency<IReferenceEventService>()
                .Received(1)
                .RaiseEventAsync(Arg.Is<ReferenceEvent>(refEvent => refEvent.Type == ReferenceEventType.Signup && refEvent.SignupInitiationPath == initiationPath));

        }
        else
        {
            await sutProvider.GetDependency<IReferenceEventService>()
                .Received(1)
                .RaiseEventAsync(Arg.Is<ReferenceEvent>(refEvent => refEvent.Type == ReferenceEventType.Signup && refEvent.SignupInitiationPath == default));
        }

        Assert.True(result.Succeeded);

    }

    [Theory]
    [BitAutoData("invalidOrgInviteToken")]
    [BitAutoData("nullOrgInviteToken")]
    [BitAutoData("nullOrgUserId")]
    public async Task RegisterUserWithOptionalOrgInvite_MissingOrInvalidOrgInviteDataWithDisabledOpenRegistration_ThrowsBadRequestException(string scenario,
        SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash, OrganizationUser orgUser, string orgInviteToken, Guid? orgUserId)
    {
        // Arrange
        sutProvider.GetDependency<IGlobalSettings>()
            .DisableUserRegistration.Returns(true);

        switch (scenario)
        {
            case "invalidOrgInviteToken":
                orgUser.Email = null; // make org user not match user and thus make tokenable invalid
                var orgInviteTokenable = new OrgUserInviteTokenable(orgUser);

                sutProvider.GetDependency<IDataProtectorTokenFactory<OrgUserInviteTokenable>>()
                    .TryUnprotect(orgInviteToken, out Arg.Any<OrgUserInviteTokenable>())
                    .Returns(callInfo =>
                    {
                        callInfo[1] = orgInviteTokenable;
                        return true;
                    });
                break;
            case "nullOrgInviteToken":
                orgInviteToken = null;
                break;
            case "nullOrgUserId":
                orgUserId = default;
                break;
        }

        // Act & Assert
        var exception = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut.RegisterUserWithOptionalOrgInvite(user, masterPasswordHash, orgInviteToken, orgUserId));
        Assert.Equal("Open registration has been disabled by the system administrator.", exception.Message);
    }

    [Theory]
    [BitAutoData("invalidOrgInviteToken")]
    [BitAutoData("nullOrgInviteToken")]
    [BitAutoData("nullOrgUserId")]
    public async Task RegisterUserWithOptionalOrgInvite_MissingOrInvalidOrgInviteDataWithEnabledOpenRegistration_ThrowsBadRequestException(string scenario,
        SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash, OrganizationUser orgUser, string orgInviteToken, Guid? orgUserId)
    {
        // Arrange
        sutProvider.GetDependency<IGlobalSettings>()
            .DisableUserRegistration.Returns(false);

        string expectedErrorMessage = null;
        switch (scenario)
        {
            case "invalidOrgInviteToken":
                orgUser.Email = null; // make org user not match user and thus make tokenable invalid
                var orgInviteTokenable = new OrgUserInviteTokenable(orgUser);

                sutProvider.GetDependency<IDataProtectorTokenFactory<OrgUserInviteTokenable>>()
                    .TryUnprotect(orgInviteToken, out Arg.Any<OrgUserInviteTokenable>())
                    .Returns(callInfo =>
                    {
                        callInfo[1] = orgInviteTokenable;
                        return true;
                    });

                expectedErrorMessage = "Organization invite token is invalid.";
                break;
            case "nullOrgInviteToken":
                orgInviteToken = null;
                expectedErrorMessage = "Organization user id cannot be provided without an organization invite token.";
                break;
            case "nullOrgUserId":
                orgUserId = default;
                expectedErrorMessage = "Organization invite token cannot be validated without an organization user id.";
                break;
        }

        user.ReferenceData = null;

        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user, masterPasswordHash)
            .Returns(IdentityResult.Success);

        // Act
        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            sutProvider.Sut.RegisterUserWithOptionalOrgInvite(user, masterPasswordHash, orgInviteToken, orgUserId));
        Assert.Equal(expectedErrorMessage, exception.Message);
    }

    // RegisterUserViaEmailVerificationToken
    [Theory]
    [BitAutoData]
    public async Task RegisterUserViaEmailVerificationToken_Succeeds(SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash, string emailVerificationToken, bool receiveMarketingMaterials)
    {
        // Arrange
        sutProvider.GetDependency<IDataProtectorTokenFactory<RegistrationEmailVerificationTokenable>>()
            .TryUnprotect(emailVerificationToken, out Arg.Any<RegistrationEmailVerificationTokenable>())
            .Returns(callInfo =>
            {
                callInfo[1] = new RegistrationEmailVerificationTokenable(user.Email, user.Name, receiveMarketingMaterials);
                return true;
            });

        sutProvider.GetDependency<IUserService>()
            .CreateUserAsync(user, masterPasswordHash)
            .Returns(IdentityResult.Success);

        // Act
        var result = await sutProvider.Sut.RegisterUserViaEmailVerificationToken(user, masterPasswordHash, emailVerificationToken);

        // Assert
        Assert.True(result.Succeeded);

        await sutProvider.GetDependency<IUserService>()
            .Received(1)
            .CreateUserAsync(Arg.Is<User>(u => u.Name == user.Name && u.EmailVerified == true), masterPasswordHash);

        await sutProvider.GetDependency<IMailService>()
            .Received(1)
            .SendWelcomeEmailAsync(user);

        await sutProvider.GetDependency<IReferenceEventService>()
            .Received(1)
            .RaiseEventAsync(Arg.Is<ReferenceEvent>(refEvent => refEvent.Type == ReferenceEventType.Signup && refEvent.ReceiveMarketingEmails == receiveMarketingMaterials));
    }

    [Theory]
    [BitAutoData]
    public async Task RegisterUserViaEmailVerificationToken_InvalidToken_ThrowsBadRequestException(SutProvider<RegisterUserCommand> sutProvider, User user, string masterPasswordHash, string emailVerificationToken, bool receiveMarketingMaterials)
    {
        // Arrange
        sutProvider.GetDependency<IDataProtectorTokenFactory<RegistrationEmailVerificationTokenable>>()
            .TryUnprotect(emailVerificationToken, out Arg.Any<RegistrationEmailVerificationTokenable>())
            .Returns(callInfo =>
            {
                callInfo[1] = new RegistrationEmailVerificationTokenable("wrongEmail@test.com", user.Name, receiveMarketingMaterials);
                return true;
            });

        // Act & Assert
        var result = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut.RegisterUserViaEmailVerificationToken(user, masterPasswordHash, emailVerificationToken));
        Assert.Equal("Invalid email verification token.", result.Message);

    }

}
