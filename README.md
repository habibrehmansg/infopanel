## InfoPanel

[Releases][release] | [Reddit][reddit] | [Website][website] | [Forum][forum] | [Discord][discord] | [Microsoft Store][msstore] | [License][license]

![example branch parameter](https://github.com/habibrehmansg/infopanel/actions/workflows/dotnet-desktop.yml/badge.svg?branch=main) 

InfoPanel is a desktop visualization software designed to work with HWiNFO sensors via Shared Memory (SHM). It allows users to display system information on their desktop or external displays, including USB-only LCDs like BeadaPanel.

## Features

- Utilizes HWiNFO sensors for comprehensive system monitoring
- Supports USB-only LCDs via WinUSB API
- GIF support for dynamic visualizations
- Faster refresh rates compared to similar solutions
- Customizable display options

## Motivation

InfoPanel was developed to leverage HWiNFO's extensive sensor capabilities while providing a flexible display solution for USB LCDs. It aims to offer more features and sensor options than existing alternatives like AIDA.

## Usage

1. Install HWiNFO and enable Shared Memory support
2. Install InfoPanel from the Microsoft Store or the website
3. Configure InfoPanel to display your desired sensors and information
4. Customize the appearance and layout to suit your preferences

## Development

InfoPanel was developed using C# for the UI, with assistance from ChatGPT to accelerate the development process. It represents the developer's first foray into C# UI development, coming from a Java backend background.

## Plugins

- [InfoPanel Spotify Plugin](https://github.com/F3NN3X/InfoPanel.Spotify) - Adds Spotify integration to InfoPanel
- [InfoPanel FPS Plugin](https://github.com/F3NN3X/InfoPanel.FPS) - Displays FPS and gaming performance metrics

## Demo
![InfoPanel Demo](https://imgur.com/a/custom-hwinfo-sensorpanel-on-beadapanel-via-usb-with-high-fps-refresh-rate-gif-support-0kDjbfT#KbPgz5E)

*A demonstration of InfoPanel in action on a BeadaPanel USB LCD*

## License

InfoPanel is licensed under GPL 3.0

---

InfoPanel is not affiliated with HWiNFO. HWiNFO is a registered trademark of its respective owners.

![InfoPanel](https://images-eds-ssl.xboxlive.com/image?url=4rt9.lXDC4H_93laV1_eHM0OYfiFeMI2p9MWie0CvL99U4GA1gf6_kayTt_kBblFwHwo8BW8JXlqfnYxKPmmBfi569Wnp8TEITjNxN843cbSYSiHkAaYYuOxjrAXsLUjKUZ4nxXfJmTW9jKbW6F0uycsBYU0tD3WyFl2.JfBRRY-&format=source)

<!--
References
-->

[gears]: https://abhitronix.github.io/vidgear/latest/gears
[reddit]: https://www.reddit.com/r/InfoPanel/
[website]: https://www.reddit.com/r/InfoPanel/
[forum]: https://www.hwinfo.com/forum/threads/infopanel-desktop-visualisation-software.8673/
[discord]: https://discord.gg/cQnjdMC7Qc
[msstore]: https://apps.microsoft.com/store/detail/XPFP7C8H5446ZD
[release]: https://github.com/habibrehmansg/infopanel/releases
[license]: https://github.com/habibrehmansg/infopanel/blob/main/LICENSE
