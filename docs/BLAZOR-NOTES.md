# Learning Blazor: notes from moving SecretariArt off MVC

Milestone 7 moves SecretariArt's front end from ASP.NET Core MVC (controllers and Razor views) to Blazor, one small
step at a time. Each section below is one step: the Blazor ideas it introduces, where to find them in our code, and the
MVC piece they replaced. Paths are relative to `src/SecretariArt/`.

The plan in one paragraph: pages render on the server, like MVC views do ("static server-side rendering", static SSR).
Interactivity is added only where it helps, as "islands": **Interactive Server** where the page needs live server data,
and **WebAssembly** where the work can happen in the browser. MVC and Blazor run side by side until the last MVC page
has moved, and URLs never change.

## Contents

1. [Hosting Blazor next to MVC, and the first page](#1-hosting-blazor-next-to-mvc-and-the-first-page)
2. [Layouts and navigation as components](#2-layouts-and-navigation-as-components)
3. [The first form: Forgot your password?](#3-the-first-form-forgot-your-password)
4. [Signing in and out](#4-signing-in-and-out)

---

## 1. Hosting Blazor next to MVC, and the first page

**What moved:** the error pages (`/error/404`, `/error/429`…): `Controllers/ErrorController.cs` and
`Views/Error/Show.cshtml` became `Components/Pages/Error.razor`.

### A component is a class
A `.razor` file compiles to a C# class (`Error.razor` becomes `SecretariArt.Components.Pages.Error`). The markup
renders it, and the `@code { }` block is the rest of the class: fields, properties and methods. An MVC view has a
controller in front of it that gets the data, then the view displays it. A component does both itself: it can
`@inject` services and load its own data. (From step 5 on we'll still keep business rules in the services, so pages
stay thin.)

### `@page` makes a component a page
`@page "/error/{StatusCode:int}"` registers the component as an endpoint for that URL. That is what
`[Route("error/{statusCode:int}")]` did on the controller action. The route values arrive as parameters: a property
with `[Parameter]` and the same name (`StatusCode`). The `:int` constraint works the same as in MVC routes.

### The root: `App.razor`, `Routes.razor`, `_Imports.razor`
- `Components/App.razor` is the whole HTML document: `<html>`, `<head>`, `<body>`. It is what `_Head.cshtml` and the
  `<html>` part of `_Layout.cshtml` were. `<HeadOutlet />` is where pages put their `<title>`: a page writes
  `<PageTitle>Not found · SecretariArt</PageTitle>`, where an MVC view wrote `ViewData["Title"]`.
- `Components/Routes.razor` holds the router. It finds the component whose `@page` matches the URL and renders it in
  its layout (`RouteView`, with `DefaultLayout` for pages that don't name one).
- `Components/_Imports.razor` lists the `@using`s every component gets, like `Views/_ViewImports.cshtml`.
- `<script src="_framework/blazor.web.js">` is Blazor's script. For static pages it adds "enhanced navigation": links
  and form posts swap the page content without a full reload. Interactivity comes later.

### Layouts are components too
`Components/Layout/AuthLayout.razor` is the Blazor `_AuthLayout.cshtml`. A layout inherits `LayoutComponentBase`, and
the page appears where it writes `@Body` (MVC: `@RenderBody()`). A page picks its layout with `@layout AuthLayout`
(MVC: `Layout = "_AuthLayout"`). One difference: the `<body>` tag belongs to `App.razor`, not to the layout. The old
`body.auth` background became a wrapper, `<div class="auth-screen">`. The logo partial became a component,
`Components/Shared/Logo.razor`, used as `<Logo />`.

### Where the request is: `[CascadingParameter] HttpContext`
In static SSR a component renders inside an ordinary HTTP request. A component can ask for that request with
`[CascadingParameter] public HttpContext HttpContext { get; set; }`, which is how the error page sets
`Response.StatusCode` and the `Retry-After` header, as the controller did. (This only works in static rendering:
interactive components run outside any request, which matters in step 18.)

### Component lifecycle: `OnInitialized`
Work before the first render goes in `OnInitialized()` (or `OnInitializedAsync()` for database work). It plays the
role of the controller action's body.

### Wiring it up (`Program.cs`, `Extensions/ServiceCollectionExtensions.cs`)
- `services.AddRazorComponents()` registers Blazor.
- `app.MapRazorComponents<App>()` turns every `@page` component into an endpoint, next to `MapControllerRoute`. A URL is
  served by whichever one claims it, so each step deletes the MVC action when its page moves.
- `app.UseAntiforgery()` checks the anti-forgery token on Blazor form posts. MVC forms use a global filter instead; it
  goes away when MVC does.

### Two gotchas, both about error pages (`Extensions/ErrorPageExtensions.cs`)
1. **The replayed request keeps its method.** The error page is reached by re-running the failed request with a new
   path (`UseStatusCodePagesWithReExecute`). That request is often a POST, for example a sign-in refused by a rate
   limit. A Blazor page only accepts a POST that names one of its forms (step 3), so a small middleware turns re-run
   requests into GETs.
2. **One Blazor render per set of request services.** Re-running into a Blazor page in the same request scope fails
   with "'HttpNavigationManager' already initialized". .NET 10's `createScopeForStatusCodePages: true` gives the
   re-run its own services. Also, when a Blazor page answers 404 it writes no body on purpose, so the status code
   page can fill it in. That's why the 404 still shows the friendly page.

**Try it:** open http://localhost:5100/error/404, then view the source and find `blazor.web.js`. The tests are in
`tests/SecretariArt.Tests/Integration/HostingTests.cs` (`ErrorPages_ShowTheFriendlyPage_…`).

---

## 2. Layouts and navigation as components

**What moved:** the "no access" page (`/account/access-denied`) became `Components/Pages/AccessDenied.razor`, and it
replaces `AccountController.AccessDenied` and its view. For it, the app's main layout and navigation now exist as
components.

### The main layout: `Components/Layout/MainLayout.razor`
This is the Blazor `_Layout.cshtml`: skip link, navigation bar, account menu, and `<main>` with `@Body` where the page
goes. It's the router's `DefaultLayout` (`Routes.razor`), so every Blazor page gets it unless it names another one.
The admin layout (sidebar, organisation switcher) comes with the first admin page, in step 9.

### A view component becomes a plain component: `MainNav.razor`
MVC needed a `ViewComponent` class plus a view (`ViewComponents/MainNavViewComponent.cs`,
`Views/Shared/Components/MainNav/Default.cshtml`) to give the layout a part with its own data. In Blazor every component
can do that. `MainNav` injects its services (`@inject ICurrentUserMemberships …`), loads the roles and the unread count
in `OnInitializedAsync`, and the layout uses it as `<MainNav />`. The MVC pair stays until the last MVC page has gone.

### `NavLink`: the active link from the URL
`<NavLink class="nav-link" href="admin">` renders an `<a>` and adds the `active` class when the current URL starts with
its `href`. `Match="NavLinkMatch.All"` on Home means "only exactly /". The MVC version compared controller names, and
needed a list of every admin controller to keep "Administration" lit. A prefix match on `/admin` does that for free.

### Choosing a layout at run time: `LayoutView` (`AnyoneLayout.razor`)
`@layout` is fixed when the component is compiled, but the error and no-access pages should show the navigation to
signed-in people and the sign-in layout to visitors. `AnyoneLayout` is a layout whose only job is to choose:
`<LayoutView Layout="@(signedIn ? typeof(MainLayout) : typeof(AuthLayout))">@Body</LayoutView>`. Pages that need it
write `@layout AnyoneLayout`.

### Forms that post to MVC: `<AntiforgeryToken />`
The account menu's "Sign out" is still an MVC action until step 4. The Blazor layout posts to it with a plain HTML
`<form action="account/logout" method="post">`. `<AntiforgeryToken />` writes the same hidden
`__RequestVerificationToken` that MVC forms carry, because both use ASP.NET Core's one anti-forgery system, so MVC
accepts it. `data-enhance="false"` makes it a normal browser post: Blazor's enhanced form handling is for posts that a
Blazor page answers. The test is `BlazorPages_UseTheMainLayout_WhoseSignOutWorks` in `HostingTests.cs`.

### Scripts: once per page load, listening on the document (`wwwroot/js/site.js`)
With enhanced navigation, Blazor swaps the page content in place: `<script>` blocks inside a page don't run again,
and a script that attached handlers to elements would lose them. So shared behaviour goes in `site.js`, loaded once by
`App.razor` (and by the MVC `_Layout`), and listens on `document`, which never changes. The data-confirm check ("Delete
Main stage?") moved there from an inline script. It listens in the capture phase and stops the event when the answer
is "Cancel", so Blazor's own submit handling never sees a refused form. Bootstrap's menus work the same way, since they
listen on the document too.

**Try it:** sign in as Eleni and open http://localhost:5100/admin/2/members (Patras isn't hers). The no-access page
has her navigation and account menu, and "Sign out" works from it.

---

## 3. The first form: "Forgot your password?"

**What moved:** `/account/forgot-password` and its "Check your email" answer became
`Components/Pages/Account/ForgotPassword.razor`. It replaces the two `AccountController.ForgotPassword` actions and the
views `ForgotPassword.cshtml` and `ForgotPasswordSent.cshtml`. Sending the link moved into a service,
`Services/PasswordResetService.cs`, because a page, like a controller, shouldn't hold business logic.

### `EditForm` and `[SupplyParameterFromForm]`
```razor
<EditForm Model="Input" FormName="forgot-password" OnValidSubmit="SendAsync" method="post">
    <DataAnnotationsValidator />
    <InputText id="email" @bind-Value="Input!.Email" class="form-control" />
    <ValidationMessage For="() => Input!.Email" />
</EditForm>

@code {
    [SupplyParameterFromForm] private ForgotPasswordViewModel? Input { get; set; }
    protected override void OnInitialized() => Input ??= new ForgotPasswordViewModel();
}
```
- On a plain **GET**, `Input` is null, so `OnInitialized` creates an empty model, or one with the email from the link.
- On a **POST**, Blazor fills `Input` from the posted fields **before** `OnInitialized` runs, so `??=` keeps it. Then the
  form validates it and calls `OnValidSubmit`, or shows the messages. This replaces MVC's model binding of the action's
  `model` parameter and the `if (!ModelState.IsValid) return View(model);` dance.
- The same view model (`ViewModels/AccountViewModels.cs`) and its `[Required]` and `[EmailAddress]` attributes are
  reused: `<DataAnnotationsValidator />` reads them.
- `InputText` with `@bind-Value` writes the `<input>` with the current value and the right `name`. `ValidationMessage`
  shows that field's errors. `class="invalid-feedback d-block"` is passed on to each message, so it looks as before.

### What the browser receives
View the page's source and you'll find:
```html
<form method="post" action="/account/forgot-password?email=…">
  <input type="hidden" name="_handler" value="forgot-password" />
  <input type="hidden" name="__RequestVerificationToken" value="…" />
  <input id="email" name="Input.Email" class="form-control valid" value="…" />
```
- **`_handler`** is the `FormName`. One page can hold several forms, and Blazor uses this to know which one was posted.
  A POST without it is refused, which is why error pages are re-run as GETs (step 1).
- **The anti-forgery token** is added by `EditForm` itself; there's no `asp-antiforgery` or global filter to think about.
- **Field names** carry the property's name: `Input.Email`. That is why the tests now post `Input.Email`.
- No `action` was written: a Blazor form posts back to its own page, query string included.

### Validation happens on the server now
The MVC pages used jQuery Validation to show messages before posting. Static Blazor pages have no client-side
validation: the form posts, the server checks it, and the page comes back with the messages. On a fast site that's a
fraction of a second, and it's the same check either way. (Interactive islands, from step 18, validate as you type.)

### The page renders differently after a post
"Check your email" isn't a second page or a redirect. `SendAsync` sets `sentTo`, and the markup shows the other half
(`@if (sentTo == null) { … } else { … }`). That's what the MVC action did with `return View("ForgotPasswordSent", …)`.

### Query strings: `[SupplyParameterFromQuery]`
`[SupplyParameterFromQuery(Name = "email")] private string? EmailFromLink { get; set; }` reads `?email=` from the
sign-in page's "Forgot your password?" link. MVC got it as an action parameter.

### A component for reused markup: `<EmailText Value="…" />`
`@Html.Email(...)` was an MVC HTML helper: it wraps email addresses at "@" and ".". Components can't use HTML helpers,
so the splitting moved into a plain method (`HtmlEmailExtensions.BreakPoints`), used by both the helper and the new
`Components/Shared/EmailText.razor`. A component parameter (`[Parameter] public string? Value`) is what a partial's
`@model` was.

### Rate limits on a Blazor page (`Extensions/RateLimiting.cs`)
`@attribute [EnableRateLimiting(RateLimitPolicies.AccountRecovery)]` puts the limit on the page's endpoint, and one
endpoint answers both the GET and the POST. With MVC the attribute sat on the POST action alone. So the form limits now
let GETs through, and only posting counts: opening the page five times doesn't use up the attempts
(`RecoveryLimitTests`). The invite-code limit is the exception: it still counts GETs, because `/join/{code}` links are
how codes would be guessed.

### The tests' form helper (`tests/.../Integration/SecretariArtFactory.cs`)
`SubmitFormAsync` used to copy the first anti-forgery token on the page. Now it finds the form being submitted: by its
action, or by `_handler` for one of several Blazor forms on a page. It copies that form's token and form name, then
the given fields. MVC and Blazor pages are tested the same way.

**Try it:** open http://localhost:5100/account/forgot-password, post it empty, then with an address, and view the
source at each stage.

---

## 4. Signing in and out

**What moved:** sign in, registration and reset password became `Components/Pages/Account/Login.razor`,
`Register.razor` and `ResetPassword.razor`. Sign-out became a small endpoint (`Extensions/AccountEndpoints.cs`).
`AccountController` and its views are gone.

### Why sign-in pages must be static
Signing in writes the sign-in cookie into the response: `SignInManager.PasswordSignInAsync` sets a `Set-Cookie`
header. With static rendering, a form's handler runs inside the HTTP request, before any of the page has been sent,
so headers can still be written. That's why `SignInManager` is used exactly as the controller used it. An interactive
component (from step 18) runs over a live connection after the page has loaded; there's no response left to put a
cookie in. Blazor's own Identity template keeps its account pages static for the same reason.

### Redirects: `NavigationManager.NavigateTo`
`Navigation.NavigateTo("/")` replaces `return RedirectToAction(…)` and `LocalRedirect(…)`. During static rendering it
stops the component (by throwing an exception that Blazor catches, so never wrap it in a `try/catch`), and Blazor
answers with a 302. Its `Location` is absolute (`http://localhost/`), where MVC's was `/`; browsers don't mind.

### `returnUrl` must stay inside the site (`Extensions/LocalUrls.cs`)
`[SupplyParameterFromQuery] private string? ReturnUrl` reads `?returnUrl=…`. Because the form posts back to its own
URL, query string included, the value is there on the POST too. Components have no `Url.IsLocalUrl`, so `LocalUrls`
applies the same rule: `/path` yes, `//other-site` and `/\other-site` no.

### Adding errors after validation: `EditContext` and `ValidationMessageStore`
Some errors only appear after the form is valid: Identity refuses the email ("already an account") or the password
rules. MVC's answer was `ModelState.AddModelError(field, message)`. Blazor's is:
```csharp
editContext = new EditContext(Input);                      // in OnInitialized
serverErrors = new ValidationMessageStore(editContext);
…
serverErrors.Add(() => Input.Email, "There's already an account with this email.");
editContext.NotifyValidationStateChanged();                // in the handler
```
The form then takes `EditContext="editContext"` instead of `Model="Input"`, and `<ValidationMessage For="() => Input.Email">`
shows these messages beside the ones from `DataAnnotationsValidator`. Errors that belong to no field ("Wrong email or
password") are a plain `problem` string shown as an alert, which is what MVC's `asp-validation-summary="ModelOnly"` did.

### Passwords must never come back in the page
`InputText` writes the field's current value into the HTML, and that includes `type="password"`. MVC's
`<input asp-for="Password">` never wrote a password's value. So after a refused form (`OnInvalidSubmit`, or a failed
sign-in) the pages clear the password fields before rendering. The test is `RefusedForms_NeverSendTheTypedPasswordBack`.

### Messages across a redirect: `FlashMessageService` and `<FlashMessages />`
"Your password has been changed" is set by the reset page and shown by the sign-in page after a redirect. MVC used
`TempData["Message"]`. The new `FlashMessageService` keeps using TempData, under the same keys, so MVC and Blazor pages
can pass messages to each other while both exist. One difference: MVC saves TempData after every action by itself,
and outside MVC it has to be saved by hand (`tempData.Save()`). `<FlashMessages />` shows and removes them: it's in
`MainLayout`, and on the sign-in page.

### Sign-out as an endpoint (`MapPost`)
Sign-out has nothing to show, so it isn't a component. It's a "minimal API" endpoint:
`app.MapPost("/account/logout", async (HttpContext context, SignInManager<ApplicationUser> signIn) => …)`. It needs
an anti-forgery check, so it's marked with `RequireAntiforgeryTokenAttribute`. `UseAntiforgery()` then checks the
token, but for a plain endpoint it only *records* the result: Blazor pages and form binding refuse a bad token by
themselves, and this handler has to look (`IAntiforgeryValidationFeature.IsValid`) and answer 400.

### One mistake worth knowing
I first wrote the labels as `<DisplayName For="() => Input.Email" />`, expecting a component that reads
`[Display(Name = …)]`. Blazor has no such component. The compiler only warned ("Found markup element with unexpected
name") and rendered it as an unknown, empty HTML element: the labels were blank, and the tests still passed. Treat
Razor warnings (RZ10012) as errors. The labels are plain text now.

**Try it:** sign in with a wrong password and view the source (the password field has no value). Then use "Forgot your
password?", follow the link from `src/SecretariArt/App_Data/mail`, and see the message on the sign-in page.
