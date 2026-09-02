# LoadingIndicators.Avalonia

> WSGM vendors these sources directly into its application assembly. The usage
> example below therefore uses WSGM's resource URI rather than a separate
> `LoadingIndicators.Avalonia.dll`.

[![Nuget](https://img.shields.io/nuget/v/LoadingIndicators.Avalonia)](https://www.nuget.org/packages/LoadingIndicators.Avalonia)

![Demo](https://raw.githubusercontent.com/moviegear/LoadingIndicators.Avalonia/master/.github/demo.gif)

[Online demo](https://moviegear.github.io/LoadingIndicators.Avalonia)

LoadingIndicators.Avalonia is an adaptation for Avalonia of the [LoadingIndicators.WPF](https://github.com/zeluisping/LoadingIndicators.WPF) collection of 9 animated loading indicators.


## Themes
- Arc
- Arcs
- Arcs Ring
- Double Bounce
- FlipPlane
- Pulse
- Ring
- Three Dots
- Wave

## Features
- Easy activation/deactivation
- Easy theme change
- Adjustable animation speed

## Usage
1. Add resources in App.axaml
```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceInclude Source="avares://WSGM/ThirdParty/LoadingIndicators/LoadingIndicators.axaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```
2. Include namespace
```xml
<Window ...
        xmlns:li="using:LoadingIndicators.Avalonia">
```
3. Add indicator and select mode
```xml
<li:LoadingIndicator IsActive="{Binding IsBusy}" Mode="Arcs" SpeedRatio="1.2" />
```
