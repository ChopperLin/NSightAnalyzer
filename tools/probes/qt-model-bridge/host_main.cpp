#include <QApplication>
#include <QTimer>

int main(int argc, char** argv)
{
    QApplication application(argc, argv);
    QTimer::singleShot(2000, &application, &QApplication::quit);
    return application.exec();
}
