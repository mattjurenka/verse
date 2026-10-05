{
  description = "Valheim server plugin dev shell (BepInEx, .NET Framework 4.6.2 target)";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = nixpkgs.legacyPackages.${system};

        # The Valheim dedicated server is a proprietary Unity binary that expects an FHS
        # filesystem, so it cannot run directly on NixOS. This wraps it in one.
        #
        # It is a headless build but still links UnityPlayer.so, which pulls in the graphics
        # and windowing stack whether or not anything is drawn - hence the long list.
        #
        # Usage:  nix run .#fhs -- <command> [args...]
        #   e.g.  nix run .#fhs -- ./valheim_server.x86_64 -name test ...
        # ./server.sh does this for you.
        valheimFhs = pkgs.buildFHSEnv {
          name = "valheim-fhs";

          targetPkgs = p: with p; [
            p7zip unzip lsb-release which coreutils bash
            fontconfig freetype
          ];

          multiPkgs = p: with p; [
            # graphics / windowing: UnityPlayer.so links these even headless
            libglvnd libGL libGLU vulkan-loader mesa
            libx11 libxcursor libxrandr libxi libxext
            libxrender libxfixes libxdamage libxcomposite
            libxtst libxscrnsaver libxcb libxshmfence
            libice libsm libxkbcommon wayland
            # misc runtime
            openssl zlib icu libuuid libsecret libnotify dbus cups expat
            nss nspr krb5 alsa-lib libpulseaudio udev harfbuzz libxml2_13
            libdrm libva
            # the server ships its own steamclient.so, which wants these
            libcap attr
          ];

          # Pass whatever we were given straight through, so this works as a prefix.
          runScript = pkgs.writeShellScript "valheim-fhs-run" ''
            if [ "$#" -eq 0 ]; then exec bash; fi
            exec "$@"
          '';
        };
      in {
        packages.fhs = valheimFhs;
        apps.fhs = {
          type = "app";
          program = "${valheimFhs}/bin/valheim-fhs";
        };

        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            dotnet-sdk_8   # builds the plugin (targets net462 via reference assemblies)
            ilspycmd       # decompile assembly_valheim.dll to check APIs
            mono           # monodis / quick IL inspection
            binutils       # strings, nm
            file
            unzip
            curl
            jq
          ];

          # dotnet on NixOS: keep it from phoning home and from needing a writable /usr
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";

          shellHook = ''
            export VALHEIM_DIR="$HOME/.local/share/Steam/steamapps/common/Valheim"
            export VALHEIM_MANAGED="$VALHEIM_DIR/valheim_Data/Managed"
            export DOTNET_ROOT="${pkgs.dotnet-sdk_8}/share/dotnet"
            export NUGET_PACKAGES="$PWD/.nuget"
            echo "valheim-plugin devshell"
            echo "  VALHEIM_DIR     = $VALHEIM_DIR"
            echo "  dotnet          = $(dotnet --version 2>/dev/null)"
            if [ ! -d "$VALHEIM_MANAGED" ]; then
              echo "  WARNING: game Managed dir not found"
            fi
          '';
        };

        # The marketing/docs site in site/ (React + Vite + Tailwind + shadcn/ui, deployed to
        # Cloudflare Pages) is a separate concern from the .NET plugins and needs none of the
        # above - just Node and pnpm. `nix develop .#site`.
        devShells.site = pkgs.mkShell {
          packages = [ pkgs.nodejs_22 pkgs.pnpm ];
        };
      });
}
