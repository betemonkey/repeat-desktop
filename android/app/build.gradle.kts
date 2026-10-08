plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.betemonkey.onrepeat"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.betemonkey.onrepeat"
        minSdk = 26
        targetSdk = 35
        versionCode = 3
        versionName = "1.2"
    }

    // the phone page itself: the same files GitHub Pages serves, bundled as is
    sourceSets["main"].assets.srcDir(rootProject.file("../mobile"))

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
}
