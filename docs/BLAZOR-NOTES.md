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
