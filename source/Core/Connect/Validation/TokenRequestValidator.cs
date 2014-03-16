﻿using System;
using System.Collections.Specialized;
using Thinktecture.IdentityServer.Core.Connect.Models;
using Thinktecture.IdentityServer.Core.Connect.Services;
using Thinktecture.IdentityServer.Core.Plumbing;
using Thinktecture.IdentityServer.Core.Services;

namespace Thinktecture.IdentityServer.Core.Connect
{
    public class TokenRequestValidator
    {
        private ICoreSettings _coreSettings;
        private ILogger _logger;
        private IAuthorizationCodeStore _authorizationCodes;
        private IUserService _profile;

        private ValidatedTokenRequest _validatedRequest;
        private IAssertionGrantValidator _assertionValidator;
        private ICustomRequestValidator _customRequestValidator;

        public ValidatedTokenRequest ValidatedRequest
        {
            get
            {
                return _validatedRequest;
            }
        }

        public TokenRequestValidator(ICoreSettings coreSettings, ILogger logger, IAuthorizationCodeStore authorizationCodes, IUserService profile, IAssertionGrantValidator assertionValidator, ICustomRequestValidator customRequestValidator)
        {
            _coreSettings = coreSettings;
            _logger = logger;
            _authorizationCodes = authorizationCodes;
            _profile = profile;
            _assertionValidator = assertionValidator;
            _customRequestValidator = customRequestValidator;
        }

        public ValidationResult ValidateRequest(NameValueCollection parameters, Client client)
        {
            _validatedRequest = new ValidatedTokenRequest();

            if (client == null)
            {
                throw new ArgumentNullException("client");
            }

            if (parameters == null)
            {
                throw new ArgumentNullException("parameters");
            }

            _validatedRequest.Raw = parameters;
            _validatedRequest.Client = client;
            _validatedRequest.Settings = _coreSettings;

            /////////////////////////////////////////////
            // check grant type
            /////////////////////////////////////////////
            var grantType = parameters.Get(Constants.TokenRequest.GrantType);
            if (grantType.IsMissing())
            {
                _logger.Error("Grant type is missing.");
                return Invalid(Constants.TokenErrors.UnsupportedGrantType);
            }

            _logger.InformationFormat("Grant type: {0}", grantType);
            _validatedRequest.GrantType = grantType;

            switch (grantType)
            {
                case Constants.GrantTypes.AuthorizationCode:
                    return RunValidation(ValidateAuthorizationCodeRequest, parameters);
                case Constants.GrantTypes.ClientCredentials:
                    return RunValidation(ValidateClientCredentialsRequest, parameters);
                case Constants.GrantTypes.Password:
                    return RunValidation(ValidateResourceOwnerCredentialRequest, parameters);
            }

            if (parameters.Get(Constants.TokenRequest.Assertion).IsPresent())
            {
                return RunValidation(ValidateAssertionRequest, parameters);
            }

            _logger.ErrorFormat("Unsupported grant_type: {0}", grantType);
            return Invalid(Constants.TokenErrors.UnsupportedGrantType);
        }

        ValidationResult RunValidation(Func<NameValueCollection, ValidationResult> validationFunc, NameValueCollection parameters)
        {
            // run standard validation
            var result = validationFunc(parameters);
            if (result.IsError)
            {
                return result;
            }

            // run custom validation
            return _customRequestValidator.ValidateTokenRequest(_validatedRequest, _profile);
        }

        private ValidationResult ValidateAuthorizationCodeRequest(NameValueCollection parameters)
        {
            /////////////////////////////////////////////
            // check if client is authorized for grant type
            /////////////////////////////////////////////
            if (_validatedRequest.Client.Flow != Flows.Code)
            {
                _logger.Error("Client not authorized for code flow");
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            /////////////////////////////////////////////
            // validate authorization code
            /////////////////////////////////////////////
            var code = parameters.Get(Constants.TokenRequest.Code);
            if (code.IsMissing())
            {
                _logger.Error("Authorization code is missing.");
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            var authZcode = _authorizationCodes.Get(code);
            if (authZcode == null)
            {
                _logger.ErrorFormat("Invalid authorization code: ", code);
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }
            else
            {
                _logger.InformationFormat("Authorization code found: {0}", code);
            }

            _authorizationCodes.Remove(code);

            /////////////////////////////////////////////
            // validate client binding
            /////////////////////////////////////////////
            if (authZcode.Client.ClientId!= _validatedRequest.Client.ClientId)
            {
                _logger.ErrorFormat("Client {0} is trying to use a code from client {1}", _validatedRequest.Client.ClientId, authZcode.Client.ClientId);
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            /////////////////////////////////////////////
            // validate code expiration
            /////////////////////////////////////////////
            if (authZcode.CreationTime.HasExpired(_validatedRequest.Client.AuthorizationCodeLifetime))
            {
                _logger.Error("Authorization code is expired");
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            _validatedRequest.AuthorizationCode = authZcode;

            /////////////////////////////////////////////
            // validate redirect_uri
            /////////////////////////////////////////////
            var redirectUri = parameters.Get(Constants.TokenRequest.RedirectUri);
            if (redirectUri.IsMissing())
            {
                _logger.Error("Redirect URI is missing.");
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            if (redirectUri != _validatedRequest.AuthorizationCode.RedirectUri.AbsoluteUri)
            {
                _logger.ErrorFormat("Invalid redirect_uri: ", redirectUri);
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            return Valid();
        }

        private ValidationResult ValidateClientCredentialsRequest(NameValueCollection parameters)
        {
            /////////////////////////////////////////////
            // check if client is authorized for grant type
            /////////////////////////////////////////////
            if (_validatedRequest.Client.Flow != Flows.ClientCredentials)
            {
                _logger.Error("Client not authorized for client credentials flow");
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            /////////////////////////////////////////////
            // check if client is allowed to request scopes
            /////////////////////////////////////////////
            if (!ValidateRequestedScopes(parameters))
            {
                _logger.Error("Invalid scopes.");
                return Invalid(Constants.TokenErrors.InvalidScope);
            }

            if (_validatedRequest.ValidatedScopes.ContainsOpenIdScopes)
            {
                _logger.Error("Client cannot request OpenID scopes in client credentials flow");
                return Invalid(Constants.TokenErrors.InvalidScope);
            }

            return Valid();
        }

        private ValidationResult ValidateResourceOwnerCredentialRequest(NameValueCollection parameters)
        {
            /////////////////////////////////////////////
            // check if client is authorized for grant type
            /////////////////////////////////////////////
            if (_validatedRequest.Client.Flow != Flows.ResourceOwner)
            {
                _logger.Error("Client not authorized for resource owner flow");
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            /////////////////////////////////////////////
            // check if client is allowed to request scopes
            /////////////////////////////////////////////
            if (!ValidateRequestedScopes(parameters))
            {
                _logger.Error("Invalid scopes.");
                return Invalid(Constants.TokenErrors.InvalidScope);
            }

            /////////////////////////////////////////////
            // check resource owner credentials
            /////////////////////////////////////////////
            var userName = parameters.Get(Constants.TokenRequest.UserName);
            var password = parameters.Get(Constants.TokenRequest.Password);

            if (userName.IsMissing() || password.IsMissing())
            {
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            var sub = _profile.Authenticate(userName, password);
            if (sub.IsPresent())
            {
                _validatedRequest.UserName = userName;

                _validatedRequest.Subject = IdentityServerPrincipal.Create(
                    sub,
                    Constants.AuthenticationMethods.Password,
                    Constants.BuiltInIdentityProvider);
            }
            else
            {
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            return Valid();
        }

        private ValidationResult ValidateAssertionRequest(NameValueCollection parameters)
        {
            var assertion = parameters.Get(Constants.TokenRequest.Assertion);
            _validatedRequest.Assertion = assertion;

            /////////////////////////////////////////////
            // check if client is authorized for grant type
            /////////////////////////////////////////////
            if (_validatedRequest.Client.Flow != Flows.Assertion)
            {
                _logger.Error("Client not authorized for assertion flow");
                return Invalid(Constants.TokenErrors.UnauthorizedClient);
            }

            /////////////////////////////////////////////
            // check if client is allowed to request scopes
            /////////////////////////////////////////////
            if (!ValidateRequestedScopes(parameters))
            {
                _logger.Error("Invalid scopes.");
                return Invalid(Constants.TokenErrors.InvalidScope);
            }

            /////////////////////////////////////////////
            // validate assertion
            /////////////////////////////////////////////
            var principal = _assertionValidator.Validate(_validatedRequest);
            if (principal == null)
            {
                _logger.Error("Invalid assertion.");
                return Invalid(Constants.TokenErrors.InvalidGrant);
            }

            _validatedRequest.Subject = principal;
            return Valid();
        }

        private bool ValidateRequestedScopes(NameValueCollection parameters)
        {
            var scopeValidator = new ScopeValidator(_logger);
            var requestedScopes = scopeValidator.ParseScopes(parameters.Get(Constants.TokenRequest.Scope));

            if (requestedScopes == null)
            {
                return false;
            }

            if (!scopeValidator.AreScopesAllowed(_validatedRequest.Client, requestedScopes))
            {
                return false;
            }
            
            if (!scopeValidator.AreScopesValid(requestedScopes, _coreSettings.GetScopes()))
            {
                return false;
            }

            _validatedRequest.Scopes = requestedScopes;
            _validatedRequest.ValidatedScopes = scopeValidator;
            return true;
        }

        private ValidationResult Valid()
        {
            return new ValidationResult
            {
                IsError = false
            };
        }

        private ValidationResult Invalid(string error)
        {
            return new ValidationResult
            {
                IsError = true,
                ErrorType = ErrorTypes.Client,
                Error = error
            };
        }
    }
}