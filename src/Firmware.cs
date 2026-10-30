using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// BIOS / UEFI updates. Windows Update hands them out as drivers of the Firmware class, but installing one
    /// only stages a capsule: the flash itself runs at the next restart, before Windows, and laptops refuse it
    /// without the charger (ASUS: "Check System Power Error: Please plug in AC Adapter"). So they are never
    /// ticked by default, need their own confirmation and are installed only on mains power.
    /// </summary>
    static class Firmware
    {
        public const string ClassGuid = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";

        static readonly Regex Words = new Regex(@"\b(firmware|bios|uefi)\b", RegexOptions.IgnoreCase);

        public static bool Is(Device d)
        {
            if (d == null) return false;
            return ClassGuid.Equals(d.ClassGuid, StringComparison.OrdinalIgnoreCase) ||
                   "Firmware".Equals(d.ClassKey, StringComparison.OrdinalIgnoreCase) ||
                   d.HardwareIds.Any(id => id.StartsWith(@"UEFI\", StringComparison.OrdinalIgnoreCase));
        }

        public static bool Is(DriverUpdate u)
        {
            return "Firmware".Equals(u.ClassKey, StringComparison.OrdinalIgnoreCase) || Is(u.Device) || Words.IsMatch(u.Title);
        }

        public static bool Is(VendorUpdate v)
        {
            return Is(v.Device) || Words.IsMatch(v.Title ?? "");
        }

        public static bool Is(InfDriver d)
        {
            if (ClassGuid.Equals(d.ClassGuid, StringComparison.OrdinalIgnoreCase) || d.HardwareIds.Any(id => id.StartsWith(@"UEFI\", StringComparison.OrdinalIgnoreCase)))
                return true;
            return d.Job != null && Is(d.Job.Update);
        }

        /// <summary>Why a firmware update cannot be installed now (null: on mains with enough charge, or no battery at all).</summary>
        public static string PowerProblem()
        {
            var ps = SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0) return null;
            if (ps.PowerLineStatus != PowerLineStatus.Online)
                return L.T("Ноутбук работает от батареи. Прошивка устанавливается при перезагрузке и без зарядного устройства не запустится — " +
                           "ноутбук остановится на экране «Please plug in AC Adapter». Подключите зарядку.",
                           "The laptop runs on battery. Firmware is flashed during the restart and will not start without the charger — " +
                           "the laptop stops at a \"Please plug in AC Adapter\" screen. Plug in the charger.");
            float pct = ps.BatteryLifePercent;
            if (pct >= 0 && pct <= 1 && pct < 0.3f)
                return L.T("Батарея заряжена на " + (int)Math.Round(pct * 100) + " %. Прошивке нужно не меньше 30 % — подождите, пока ноутбук зарядится.",
                           "The battery is at " + (int)Math.Round(pct * 100) + " %. Firmware needs at least 30 % — let the laptop charge first.");
            return null;
        }

        /// <summary>
        /// Asks before firmware goes along with the other updates. True: install it. False: leave it out
        /// (also when the power is not right: the reason is shown and the rest can still go).
        /// </summary>
        public static bool Confirm(IWin32Window owner, int count)
        {
            string problem = PowerProblem();
            if (problem != null)
            {
                ThemedDialog.Info(owner, L.T("Прошивку сейчас не установить", "Firmware cannot be installed now"),
                    problem + L.T("\n\nОбновление прошивки пропущено, остальное будет установлено.", "\n\nThe firmware update is skipped; the rest is installed."), true);
                return false;
            }
            return ThemedDialog.Confirm(owner, L.T("Обновить прошивку (BIOS)?", "Update the firmware (BIOS)?"),
                L.T("Отмечено обновлений прошивки: ", "Firmware updates ticked: ") + count + ".\n" +
                L.T("Прошивка записывается при следующей перезагрузке, до запуска Windows, и занимает несколько минут; экран может гаснуть.\n" +
                    "Пока она идёт, не отключайте зарядное устройство и не выключайте компьютер — прерванная прошивка может сделать его неработоспособным.\n\n" +
                    "«Отмена» — установить остальное без прошивки.",
                    "The firmware is written at the next restart, before Windows starts, and takes a few minutes; the screen may go dark.\n" +
                    "Meanwhile do not unplug the charger or turn the computer off — an interrupted flash can leave it unable to start.\n\n" +
                    "Cancel installs the rest without the firmware."),
                L.T("Обновить прошивку", "Update firmware"), "\uE950", true);
        }

        /// <summary>Said before the restart that flashes it.</summary>
        public static string RestartNote
        {
            get
            {
                return L.T("\n\nПри перезагрузке обновится прошивка (BIOS): держите зарядное устройство подключённым и не выключайте компьютер, пока она не закончится.",
                           "\n\nThe firmware (BIOS) is updated during the restart: keep the charger plugged in and do not turn the computer off until it finishes.");
            }
        }
    }
}
