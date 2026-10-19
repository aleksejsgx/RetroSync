// UI language: English default; Italian via Settings or --lang it
using System;
using System.Collections.Generic;

namespace RetroSync
{
    static class Lang
    {
        public static string Code = "en";
        public static string Forced;   // set by --lang; wins over settings

        public static void Apply(AppSettings s)
        {
            if (!string.IsNullOrEmpty(Forced)) { Code = Forced == "it" ? "it" : "en"; return; }
            var c = (s != null && s.Ui != null && s.Ui.Language != null) ? s.Ui.Language.Trim().ToLowerInvariant() : "en";
            Code = (c == "it" || c == "ita" || c == "italian") ? "it" : "en";
        }

        public static bool IsIt { get { return Code == "it"; } }

        public static string T(string en)
        {
            string it;
            if (IsIt && Map.TryGetValue(en, out it)) return it;
            return en;
        }

        public static string T(string en, string it) { return IsIt ? it : en; }

        static readonly Dictionary<string, string> Map = Build();

        static Dictionary<string, string> Build()
        {
            var m = new Dictionary<string, string>();
            Action<string, string> a = (en, it) => { if (!m.ContainsKey(en)) m[en] = it; };
            // Nav / chrome
            a("Dashboard", "Dashboard");
            a("Settings", "Impostazioni");
            a("Backup", "Backup");
            a("Log", "Log");
            a("soon", "in arrivo");
            a("Dry-run", "Dry-run");
            a("Cancel", "Annulla");
            a("Save", "Salva");
            a("Continue", "Continua");
            a("Close", "Chiudi");
            a("Create", "Crea");
            a("Demo", "Demo");
            a("Open Log", "Apri Log");
            a("PC ready", "PC pronto");
            a("PS5 online", "PS5 online");
            a("FTP up", "FTP attivo");
            a("Latency ", "Latenza ");
            // Dashboard
            a("Overview of PC, PS5 and recent syncs", "Panoramica di PC, PS5 e ultime sincronizzazioni");
            a("Quick actions", "Azioni rapide");
            a("Every action shows a preview first — nothing is written without confirmation", "Ogni azione mostra prima l'anteprima — niente viene scritto senza conferma");
            a("Recent activity", "Attività recente");
            a("Needs attention", "Da controllare");
            a("Sync saves", "Sync salvataggi");
            a("Sync RetroArch", "Sync RetroArch");
            a("PS2 memory card", "Memory card PS2");
            a("PS5 backup", "Backup PS5");
            a("Sync all…", "Sync tutto…");
            a("Preview only", "Solo anteprima");
            a("Wake PS5", "Accendi PS5");
            a("Wake and sync", "Accendi e sincronizza");
            a("Rest mode", "Metti a riposo");
            a("Saves and states", "Salvataggi e stati");
            a("Full mirror, read-only", "Mirror completo, sola lettura");
            a("Free space", "Spazio libero");
            a("Wake", "Accensione");
            a("configured", "configurata");
            a("not configured", "non configurata");
            a("Ready", "Pronto");
            a("Online", "Online");
            a("In rest", "In riposo");
            a("FTP down", "FTP spento");
            a("Offline", "Offline");
            a("Last sync", "Ultima sincronizzazione");
            a("never", "mai");
            a("No sync recorded yet.", "Nessuna sincronizzazione registrata finora.");
            a("Errors", "Errori");
            a("Type", "Tipo");
            a("Transferred", "Trasferiti");
            a("Direction", "Direzione");
            a("No activity yet.", "Ancora nessuna attività.");
            a("Done", "Completato");
            a("Error", "Errore");
            a("Info", "Info");
            a("Conflict", "Conflitto");
            a("Merge", "Unisci");
            a("Rest", "Riposo");
            a("Just now", "adesso");
            a("just now", "adesso");
            // Settings / wake
            a("Remote power-on", "Accensione remota");
            a("PS5 Remote Play · UDP 9302 / 9295", "Riproduzione remota PS5 · UDP 9302 / 9295");
            a("Pair once (recommended)", "Associa una volta (consigliato)");
            a("Pair PS5", "Associa PS5");
            a("Or enter a wake credential", "Oppure inserisci il codice di accensione");
            a("Credential (user-credential)", "Codice (user-credential)");
            a("Behaviour", "Comportamento");
            a("After wake", "Dopo l'accensione");
            a("Sync all", "Sync tutto");
            a("Wake only", "Solo accendi");
            a("Max FTP wait (s)", "Attesa massima FTP (s)");
            a("Wait after FTP (s)", "Attesa dopo l'FTP (s)");
            a("Status check every (s)", "Controllo stato ogni (s)");
            a("Check status", "Verifica stato");
            a("Test wake", "Prova accensione");
            a("Discard changes", "Annulla modifiche");
            a("Advanced", "Avanzate");
            a("Language / Lingua", "Language / Lingua");
            a("Connection", "Connessione");
            a("Test connection", "Prova connessione");
            a("PS5 IP address", "Indirizzo IP PS5");
            a("FTP port", "Porta FTP");
            a("FTP user", "Utente FTP");
            a("FTP password", "Password FTP");
            a("FTP timeout (s)", "Timeout FTP (s)");
            a("RetroArch folder on PC", "Cartella RetroArch sul PC");
            a("RetroArch folder on PS5 (FTP)", "Cartella RetroArch PS5 (FTP)");
            a("PS5 app folder (FTP)", "Cartella app PS5 (FTP)");
            a("Memory card folder on PC", "Cartella card sul PC");
            a("Memory card folder on PS5", "Cartella card sul PS5");
            a("Card name", "Nome della card");
            a("Path to mymc.exe", "Percorso di mymc.exe");
            a("Backup folder", "Cartella backup");
            a("Backups to keep", "Backup da conservare");
            a("PS5 mirror: PC folder", "Mirror PS5: cartella PC");
            a("PS5 mirror: PS5 folder", "Mirror PS5: cartella PS5");
            a("Versions per emulator", "Versioni per emulatore");
            a("In rest mode", "In modalità riposo");
            a("Awake", "Acceso");
            a("Configured", "Configurata");
            a("Not configured · pair the PS5 once to wake it from rest mode",
              "Non configurata · associa il PS5 una volta per poterlo accendere dalla modalità riposo");
            a("Put the PS5 into rest mode?", "Mettere il PS5 a riposo?");
            a("Read PS5 status over the network (rest / awake / firmware)",
              "Leggi lo stato del PS5 via rete (riposo / acceso / firmware)");
            // Backup page
            a("Start PS5 backup", "Avvia backup PS5");
            a("Open folder", "Apri cartella");
            a("Open backup folder", "Apri cartella backup");
            a("Every day", "Ogni giorno");
            a("Every Sunday", "Ogni domenica");
            a("Copy command", "Copia comando");
            a("Create Windows task…", "Crea attività in Windows…");
            a("Run now", "Esegui ora");
            a("Unavailable", "Non disponibile");
            a("Wake the PS5 first (--wake)", "Accendi prima il PS5 (--wake)");
            a("Backups of the PS5 and emulators on the PC — nothing is ever written to the PS5",
              "Copie di sicurezza della PS5 e degli emulatori sul PC — sul PS5 non viene mai scritto nulla");
            a("Per-emulator backup", "Backup per emulatore");
            a("Never run", "Mai eseguito");
            a("Scheduling", "Pianificazione");
            a("Task created.", "Attività creata.");
            // Common actions / messages
            a("Preview", "Anteprima");
            a("Analysis", "Analisi");
            a("Sync", "Sincronizza");
            a("Syncing…", "Sincronizzazione in corso…");
            a("ERROR: ", "ERRORE: ");
            a("Pairing failed", "Associazione non riuscita");
            a("Pairing rejected", "Associazione rifiutata");
            a("Wrong or expired PIN", "PIN errato o scaduto");
            a("Open Settings", "Apri Impostazioni");
            a("Remote power-on not configured", "Accensione remota non configurata");
            a("Demo mode does not send anything.", "In modalità demo non viene inviato nulla.");
            a("Demo mode does not copy anything.", "In modalità demo non viene copiato nulla.");
            a("Demo mode does not write anything.", "In modalità demo non viene scritto nulla.");
            return m;
        }
    }
}
